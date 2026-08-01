using System.Data.Common;
using System.Reflection;
using Isas.PaymentService.Models;
using Isas.PaymentService.Services;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Diagnostics;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using PaymentService.Models;

namespace Isas.PaymentService.Tests;

// S8 P0 — 3 lỗi mất tiền tìm ra ở DB architecture review 2026-07-19.
//
// DB20 — gói không sinh credit ⇒ ledger Delta=0 ⇒ CHECK ck_credit_transactions_delta_nonzero nổ ⇒
//        tx.Commit không chạy ⇒ flip Pending→Paid ROLLBACK theo ⇒ khách ĐÃ TRẢ TIỀN mà đơn kẹt
//        Pending vĩnh viễn (deterministic ⇒ mọi đường cứu re-fail).
// DB21 — CreditReservationReconciler ghi đè snapshot cũ ⇒ XOÁ slot vừa reserve ⇒ credit bốc hơi.
// DB22 — bút toán trừ reserved_credits không guard ⇒ trừ xuống âm ⇒ CHECK nổ ⇒ rollback ⇒
//        reservation kẹt Reserved ⇒ consumer nack-requeue vô hạn ⇒ chặn CẢ queue credit.
public class MoneyLossGuardsDb20Db21Db22Tests
{
    // ───────────────────────── DB20 ─────────────────────────

    private static async Task<ProductPackage> SeedPackageAsync(
        PaymentTestDb tdb, PackageType type, int? credits, int? durationDays = null)
    {
        var pkg = new ProductPackage
        {
            Id = Guid.NewGuid(),
            Name = $"Pack {type}/{credits?.ToString() ?? "null"}",
            Type = type,
            PriceVnd = 100_000,
            InterviewCredits = credits,
            DurationDays = durationDays,
            IsActive = true,
            CreatedAt = DateTime.UtcNow
        };
        if (type == PackageType.Subscription)
        {
            pkg.PlanId = Guid.NewGuid();
            pkg.Audience = PlanAudience.B2C;
            tdb.Db.Plans.Add(new Plan
            {
                Id = pkg.PlanId.Value,
                Audience = PlanAudience.B2C,
                Code = $"db20-{pkg.Id:N}",
                Name = "DB20 subscription tier",
                Rank = 1,
                InterviewFunding = InterviewFunding.Metered,
                MonthlyQuota = 1,
                IsActive = true,
                CreatedAt = DateTime.UtcNow,
                UpdatedAt = DateTime.UtcNow
            });
        }
        tdb.Db.ProductPackages.Add(pkg);
        await tdb.Db.SaveChangesAsync();
        return pkg;
    }

    // ⚠ F8 ĐỔI TIỀN ĐỀ CỦA TEST NÀY, CÓ CHỦ Ý.
    // Bản cũ khẳng định "gói Subscription KHÔNG mua được" — đúng khi subscription chưa được xây, vì lúc
    // đó cách duy nhất để mua là chui vào đường CreditPack rồi chết ở webhook. F8 mở đường bán RIÊNG nên
    // khẳng định cần bảo vệ không còn là "không mua được" mà là bất biến THẬT của DB20:
    //
    //     Kind = CreditPack  ⇒  gói sinh credit > 0
    //
    // Gói Subscription giờ đi ra một Kind khác ⇒ dòng `credits ?? 0` ở WebhookService (thứ đẻ ra ledger
    // Delta=0 → nổ CHECK → rollback flip Pending→Paid → đơn kẹt Pending vĩnh viễn) nằm NGOÀI đường đi của
    // nó. Test viết lại để khoá đúng bất biến đó — KHÔNG phải nới assert cũ cho khỏi đỏ.
    [Fact]
    public async Task Db20_TaoDon_GoiSubscription_KhongBaoGioMangKindCreditPack()
    {
        using var tdb = new PaymentTestDb();
        var pkg = await SeedPackageAsync(tdb, PackageType.Subscription, credits: null, durationDays: 30);

        var svc = new OrderService(tdb.Db, null!, Options.Create(NewPayosSettings()), new FakeOrderCodes());

        // PayOSClient = null! → ném khi gọi cổng thanh toán. Đơn được persist TRƯỚC bước đó (hành vi sẵn
        // có của CreateOrderAsync), nên vẫn kiểm được Kind đã ghi xuống DB.
        await Assert.ThrowsAnyAsync<Exception>(() =>
            svc.CreateOrderAsync(OwnerType.User, Guid.NewGuid(),
                new DTOs.OrderRequest.CreateOrderRequest { PackageId = pkg.Id }));

        var order = Assert.Single(await tdb.Db.Orders.ToListAsync());
        Assert.Equal(OrderKind.SubscriptionPurchase, order.Kind);
    }

    // DB hardening: enum-string CHECK là hàng rào đầu tiên với dữ liệu sửa tay/legacy. Vì fixture SQLite
    // nay cũng enforce đúng ràng buộc Postgres, không thể persist enum sai rồi mới đưa vào OrderService.
    [Fact]
    public async Task Db20_ProductPackage_LoaiGoiKhongHopLe_BiChanTaiDb()
    {
        using var tdb = new PaymentTestDb();
        var pkg = await SeedPackageAsync(tdb, PackageType.OneTime, credits: 5);
        pkg.Type = (PackageType)99;
        await Assert.ThrowsAsync<DbUpdateException>(() => tdb.Db.SaveChangesAsync());
        Assert.Empty(await tdb.Db.Orders.ToListAsync());   // KHÔNG để lại đơn mồ côi
    }

    [Fact]
    public async Task Db20_TaoDon_GoiOneTimeCredits0_BiChan()
    {
        using var tdb = new PaymentTestDb();
        var pkg = await SeedPackageAsync(tdb, PackageType.OneTime, credits: 0);

        var svc = new OrderService(tdb.Db, null!, Options.Create(NewPayosSettings()), new FakeOrderCodes());

        await Assert.ThrowsAsync<InvalidOperationException>(() =>
            svc.CreateOrderAsync(OwnerType.User, Guid.NewGuid(),
                new DTOs.OrderRequest.CreateOrderRequest { PackageId = pkg.Id }));

        Assert.Empty(await tdb.Db.Orders.ToListAsync());
    }

    // Đơn CŨ (tạo trước fix) vẫn nằm trong DB → webhook PHẢI không kẹt Pending nữa.
    // Đây là hồi quy trực tiếp cho lỗi gốc: trước fix, dòng này ném và order rollback về Pending.
    [Fact]
    public async Task Db20_Webhook_DonCu_GoiKhongSinhCredit_KhongConKetPending()
    {
        using var tdb = new PaymentTestDb();
        var pkg = await SeedPackageAsync(tdb, PackageType.Subscription, credits: null, durationDays: 30);

        var ownerId = Guid.NewGuid();
        const long orderCode = 250719_0001;
        tdb.Db.Orders.Add(new Order
        {
            Id = Guid.NewGuid(),
            OwnerType = OwnerType.User,
            OwnerId = ownerId,
            Kind = OrderKind.CreditPack,
            PackageId = pkg.Id,
            Status = OrderStatus.Pending,
            AmountVnd = 100_000,
            PayosOrderCode = orderCode,
            ExpiredAt = DateTime.UtcNow.AddMinutes(30),
            CreatedAt = DateTime.UtcNow
        });
        await tdb.Db.SaveChangesAsync();

        var ctx = tdb.NewContext();
        var svc = new WebhookService(ctx, new CreditAccountService(ctx));

        // Trước fix: ném DbUpdateException (CHECK delta<>0) và order rollback về Pending.
        var outcome = await svc.ApplyPaidWebhookAsync(orderCode, "txn-1", "{}");

        Assert.Equal(WebhookApplyOutcome.Credited, outcome);

        var order = await tdb.NewContext().Orders.SingleAsync(o => o.PayosOrderCode == orderCode);
        Assert.Equal(OrderStatus.Paid, order.Status);          // KHÔNG kẹt Pending
        Assert.NotNull(order.PaidAt);

        // Không cộng credit và KHÔNG ghi ledger rác (delta=0) — để đối soát tay.
        Assert.Empty(await tdb.NewContext().CreditTransactions.ToListAsync());
        // Vẫn lưu bằng chứng gateway.
        Assert.Single(await tdb.NewContext().PaymentTransactions.ToListAsync());
    }

    // ───────────────────────── DB21 ─────────────────────────

    private static async Task ScanOnce(CreditReservationReconciler r)
    {
        var mi = typeof(CreditReservationReconciler)
            .GetMethod("ScanOnceAsync", BindingFlags.NonPublic | BindingFlags.Instance)!;
        await (Task)mi.Invoke(r, new object[] { CancellationToken.None })!;
    }

    // ĐUA THẬT: chèn một ReserveAsync vào ĐÚNG khe hở giữa CountAsync và ExecuteUpdate của reconciler.
    // Dựng bằng DbCommandInterceptor: ngay sau khi câu COUNT chạy xong, ta mô phỏng "request reserve vừa
    // commit" bằng cách nâng reserved_credits + thêm 1 reservation Reserved trên CÙNG connection.
    // Reconciler khi đó cầm snapshot ĐÃ CŨ.
    //   - KHÔNG có guard CAS: ghi đè count cũ → xoá slot vừa giữ → credit bốc hơi (đây là lỗi gốc).
    //   - CÓ guard CAS: WHERE reserved_credits = <snapshot cũ> khớp 0 row → bỏ qua, slot còn nguyên.
    // Đã kiểm chứng bằng mutation: gỡ guard khỏi production → test này ĐỎ.
    [Fact]
    public async Task Db21_Reconciler_KhongGhiDeSlotVuaReserve_KhiDuaGiuaCountVaUpdate()
    {
        using var tdb = new PaymentTestDb();
        var ownerId = Guid.NewGuid();
        var accId = Guid.NewGuid();

        tdb.Db.CreditAccounts.Add(new CreditAccount
        {
            Id = accId,
            OwnerType = OwnerType.User,
            OwnerId = ownerId,
            PaymentMode = PaymentMode.Prepaid,
            Status = CreditAccountStatus.Active,
            RemainingCredits = 10,
            ReservedCredits = 3,      // drift có thật → reconciler SẼ định ghi (không short-circuit)
            UpdatedAt = DateTime.UtcNow
        });
        await tdb.Db.SaveChangesAsync();   // 0 reservation Reserved → count=0, snapshot=3 ⇒ định set 0

        // Đua: NGAY SAU câu COUNT, một reserve commit → reserved 3→4 kèm 1 reservation Reserved.
        // Thời điểm "sau COUNT" là bắt buộc: nếu chen TRƯỚC thì count đã thấy reservation mới và việc
        // reconciler ghi count đó lại hoá đúng — không phân biệt được có guard hay không.
        // Ghi bằng SQL thô trên CHÍNH connection đang mở (SQLite không cho tạo DbContext khi reader còn
        // sống), và copy owner_type/owner_id TỪ BẢNG để khỏi phải đoán cách EF serialize Guid/enum —
        // đoán sai thì FK composite (owner_type, owner_id) → credit_accounts sẽ fail.
        var interceptor = new ReserveRacesAfterCountInterceptor(async cmd =>
        {
            await using var bump = cmd.Connection!.CreateCommand();
            bump.CommandText = """
                UPDATE credit_accounts SET reserved_credits = reserved_credits + 1;
                INSERT INTO credit_reservations
                    (id, owner_type, owner_id, session_id, status, created_at, updated_at)
                SELECT $rid, owner_type, owner_id, $sid, 'Reserved', $now, $now
                FROM credit_accounts LIMIT 1;
                """;
            AddParam(bump, "$rid", Guid.NewGuid().ToString());
            AddParam(bump, "$sid", Guid.NewGuid().ToString());
            AddParam(bump, "$now", DateTime.UtcNow.ToString("o"));
            await bump.ExecuteNonQueryAsync();
        });

        var services = new ServiceCollection();
        services.AddDbContext<PaymentDbContext>(o => o
            .UseSqlite(tdb.Connection)
            .AddInterceptors(interceptor)
            .UseSnakeCaseNamingConvention());
        using var provider = services.BuildServiceProvider();

        var r = new CreditReservationReconciler(
            provider.GetRequiredService<IServiceScopeFactory>(),
            Options.Create(new ReconcileSettings { Enabled = true, ScanIntervalSeconds = 120 }),
            NullLogger<CreditReservationReconciler>.Instance);

        await ScanOnce(r);

        Assert.True(interceptor.Fired, "Interceptor phải chen được vào giữa COUNT và UPDATE.");

        var after = await tdb.NewContext().CreditAccounts.SingleAsync(a => a.Id == accId);

        // Slot vừa reserve KHÔNG được biến mất. Không guard → bị kéo về 0 (mất credit đang giữ).
        Assert.Equal(4, after.ReservedCredits);
    }

    // Chen vào ngay sau câu COUNT reservation: mô phỏng một ReserveAsync vừa commit xong.
    private sealed class ReserveRacesAfterCountInterceptor : DbCommandInterceptor
    {
        private readonly Func<DbCommand, Task> _race;
        private bool _done;

        public bool Fired => _done;

        public ReserveRacesAfterCountInterceptor(Func<DbCommand, Task> race) => _race = race;

        public override async ValueTask<DbDataReader> ReaderExecutedAsync(
            DbCommand command, CommandExecutedEventData eventData, DbDataReader result,
            CancellationToken cancellationToken = default)
        {
            if (!_done && command.CommandText.Contains("count(", StringComparison.OrdinalIgnoreCase)
                       && command.CommandText.Contains("credit_reservations", StringComparison.OrdinalIgnoreCase))
            {
                _done = true;   // chỉ chen 1 lần
                await _race(command);
            }

            return await base.ReaderExecutedAsync(command, eventData, result, cancellationToken);
        }
    }

    // Drift thật (reserved cao hơn count) vẫn PHẢI được sửa — guard CAS không được làm reconciler tê liệt.
    [Fact]
    public async Task Db21_Reconciler_VanSuaDuocDriftThat()
    {
        using var tdb = new PaymentTestDb();
        var ownerId = Guid.NewGuid();

        tdb.Db.CreditAccounts.Add(new CreditAccount
        {
            Id = Guid.NewGuid(),
            OwnerType = OwnerType.User,
            OwnerId = ownerId,
            PaymentMode = PaymentMode.Prepaid,
            Status = CreditAccountStatus.Active,
            RemainingCredits = 10,
            ReservedCredits = 5,        // drift: không có reservation Reserved nào
            UpdatedAt = DateTime.UtcNow
        });
        await tdb.Db.SaveChangesAsync();

        var (r, provider) = BuildReconciler(tdb);
        using (provider)
        {
            await ScanOnce(r);
        }

        var after = await tdb.NewContext().CreditAccounts.SingleAsync(a => a.OwnerId == ownerId);
        Assert.Equal(0, after.ReservedCredits);
    }

    private static (CreditReservationReconciler r, ServiceProvider provider) BuildReconciler(PaymentTestDb tdb)
    {
        var services = new ServiceCollection();
        services.AddDbContext<PaymentDbContext>(o => o
            .UseSqlite(tdb.Connection)
            .UseSnakeCaseNamingConvention());
        var provider = services.BuildServiceProvider();

        var r = new CreditReservationReconciler(
            provider.GetRequiredService<IServiceScopeFactory>(),
            Options.Create(new ReconcileSettings { Enabled = true, ScanIntervalSeconds = 120 }),
            NullLogger<CreditReservationReconciler>.Instance);
        return (r, provider);
    }

    // ───────────────────────── DB22 ─────────────────────────

    // Ví đã drift về reserved=0 nhưng reservation vẫn Reserved (đúng tình huống DB21 cũ gây ra).
    // Trước fix: consume trừ 0−1 = −1 → CHECK ck_credit_accounts_non_negative nổ → tx rollback →
    // reservation kẹt Reserved → consumer nack-requeue vô hạn → CHẶN CẢ QUEUE.
    // Sau fix: guard `reserved >= 1` → bút toán bỏ qua, reservation VẪN chuyển Consumed → queue thông.
    [Fact]
    public async Task Db22_Consume_ViDriftVe0_KhongNemCheck_ReservationVanChotDuoc()
    {
        using var tdb = new PaymentTestDb();
        var (ownerId, sessionId) = await SeedDriftedReservationAsync(tdb);

        var ctx = tdb.NewContext();
        var svc = new CreditAccountService(ctx);

        var result = await svc.ConsumeAsync(sessionId);      // trước fix: ném DbUpdateException

        Assert.Equal(ConsumeOutcome.Consumed, result.Outcome);

        var res = await tdb.NewContext().CreditReservations.SingleAsync(r => r.SessionId == sessionId);
        Assert.Equal(ReservationStatus.Consumed, res.Status); // đã chốt → không redeliver vô hạn

        var acc = await tdb.NewContext().CreditAccounts.SingleAsync(a => a.OwnerId == ownerId);
        Assert.Equal(0, acc.ReservedCredits);                 // không âm
    }

    [Fact]
    public async Task Db22_Release_ViDriftVe0_KhongNemCheck_ReservationVanChotDuoc()
    {
        using var tdb = new PaymentTestDb();
        var (ownerId, sessionId) = await SeedDriftedReservationAsync(tdb);

        var ctx = tdb.NewContext();
        var svc = new CreditAccountService(ctx);

        var result = await svc.ReleaseAsync(sessionId);

        Assert.Equal(ReleaseOutcome.Released, result.Outcome);

        var res = await tdb.NewContext().CreditReservations.SingleAsync(r => r.SessionId == sessionId);
        Assert.Equal(ReservationStatus.Released, res.Status);

        var acc = await tdb.NewContext().CreditAccounts.SingleAsync(a => a.OwnerId == ownerId);
        Assert.Equal(0, acc.ReservedCredits);
    }

    private static async Task<(Guid ownerId, Guid sessionId)> SeedDriftedReservationAsync(PaymentTestDb tdb)
    {
        var ownerId = Guid.NewGuid();
        var sessionId = Guid.NewGuid();

        tdb.Db.CreditAccounts.Add(new CreditAccount
        {
            Id = Guid.NewGuid(),
            OwnerType = OwnerType.User,
            OwnerId = ownerId,
            PaymentMode = PaymentMode.Prepaid,
            Status = CreditAccountStatus.Active,
            RemainingCredits = 5,
            ReservedCredits = 0,      // DRIFT: có reservation Reserved nhưng ví ghi 0
            UpdatedAt = DateTime.UtcNow
        });
        tdb.Db.CreditReservations.Add(new CreditReservation
        {
            Id = Guid.NewGuid(),
            OwnerType = OwnerType.User,
            OwnerId = ownerId,
            SessionId = sessionId,
            Status = ReservationStatus.Reserved,
            CreatedAt = DateTime.UtcNow
        });
        await tdb.Db.SaveChangesAsync();
        return (ownerId, sessionId);
    }

    private static void AddParam(DbCommand cmd, string name, object value)
    {
        var p = cmd.CreateParameter();
        p.ParameterName = name;
        p.Value = value;
        cmd.Parameters.Add(p);
    }

    private static PayOSSettings NewPayosSettings() => new()
    {
        ReturnUrl = "https://example.test/return",
        CancelUrl = "https://example.test/cancel"
    };

    private sealed class FakeOrderCodes : IOrderCodeGenerator
    {
        public Task<long> GenerateAsync(CancellationToken ct = default) => Task.FromResult(2507190001L);
    }
}
