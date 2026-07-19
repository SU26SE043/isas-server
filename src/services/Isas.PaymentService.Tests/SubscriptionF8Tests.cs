using System.Reflection;
using Isas.PaymentService.Models;
using Isas.PaymentService.Services;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using PaymentService.Models;

namespace Isas.PaymentService.Tests;

/// <summary>
/// F8 — thuê bao (Premium B2C / membership Tháng·Năm B2B).
///
/// Trọng tâm KHÔNG phải "tính năng có chạy không" mà là **bất biến sổ cái không bị thủng**:
///   (1) `remaining + reserved = Σ credit_transactions.delta` — chỗ giữ do thuê bao tài trợ không đụng
///       vế nào trong ba bước reserve/consume/release;
///   (2) `reserved_credits = count(reservations Reserved AND funded_by='Credit')` — reconciler DB4/DB21
///       phải bỏ qua chỗ giữ của subscriber, nếu không nó tự tạo ra drift;
///   (3) đơn thuê bao KHÔNG BAO GIỜ kẹt `Pending` (lỗi DB20) vì không đi qua đường ghi ledger.
/// </summary>
public class SubscriptionF8Tests
{
    // ───────────────────────── helpers ─────────────────────────

    private static ProductPackage NewSubPackage(int durationDays = 30) => new()
    {
        Id = Guid.NewGuid(),
        Name = "Premium",
        Type = PackageType.Subscription,
        PriceVnd = 199_000,
        InterviewCredits = null,     // đúng hình dạng gói thuê bao thật (PackageService.Validate cho phép)
        DurationDays = durationDays,
        IsActive = true,
        CreatedAt = DateTime.UtcNow
    };

    private static async Task<Order> SeedPendingOrderAsync(
        PaymentTestDb tdb, OwnerType ownerType, Guid ownerId, ProductPackage pkg, OrderKind kind, long code)
    {
        tdb.Db.ProductPackages.Add(pkg);
        var order = new Order
        {
            Id = Guid.NewGuid(),
            OwnerType = ownerType,
            OwnerId = ownerId,
            Kind = kind,
            PackageId = pkg.Id,
            Status = OrderStatus.Pending,
            AmountVnd = pkg.PriceVnd,
            PayosOrderCode = code,
            ExpiredAt = DateTime.UtcNow.AddMinutes(30),
            CreatedAt = DateTime.UtcNow
        };
        tdb.Db.Orders.Add(order);
        await tdb.Db.SaveChangesAsync();
        return order;
    }

    private static async Task<CreditAccount> SeedWalletAsync(
        PaymentTestDb tdb, OwnerType ownerType, Guid ownerId, int remaining = 0,
        CreditAccountStatus status = CreditAccountStatus.Active)
    {
        var acc = new CreditAccount
        {
            Id = Guid.NewGuid(),
            OwnerType = ownerType,
            OwnerId = ownerId,
            PaymentMode = PaymentMode.Prepaid,
            Status = status,
            RemainingCredits = remaining,
            ReservedCredits = 0,
            UpdatedAt = DateTime.UtcNow
        };
        tdb.Db.CreditAccounts.Add(acc);
        await tdb.Db.SaveChangesAsync();
        return acc;
    }

    private static async Task SeedSubscriptionAsync(
        PaymentTestDb tdb, OwnerType ownerType, Guid ownerId, DateTime expiresAt,
        SubscriptionStatus status = SubscriptionStatus.Active)
    {
        tdb.Db.Subscriptions.Add(new Subscription
        {
            Id = Guid.NewGuid(),
            OwnerType = ownerType,
            OwnerId = ownerId,
            BillingCycle = BillingCycle.Monthly,
            Status = status,
            StartedAt = expiresAt.AddDays(-30),
            ExpiresAt = expiresAt,
            CreatedAt = DateTime.UtcNow
        });
        await tdb.Db.SaveChangesAsync();
    }

    private static CreditAccountService NewCreditSvc(PaymentTestDb tdb, PaymentDbContext? db = null)
    {
        var ctx = db ?? tdb.Db;
        return new CreditAccountService(
            ctx, null, Options.Create(new BillingSettings { FreeTrialCredits = 0 }),
            new SubscriptionService(ctx));
    }

    // ───────────────────── mua gói / kích hoạt ─────────────────────

    // Kỳ vọng chính của ô F8: webhook Paid → sub Active, KHÔNG ghi credit_transactions, đơn Paid.
    [Fact]
    public async Task MuaGoiThueBao_WebhookPaid_KichHoatKyHan_KhongGhiSoCai()
    {
        using var tdb = new PaymentTestDb();
        var owner = Guid.NewGuid();
        var pkg = NewSubPackage(30);
        var order = await SeedPendingOrderAsync(
            tdb, OwnerType.User, owner, pkg, OrderKind.SubscriptionPurchase, 111);

        var svc = new WebhookService(tdb.Db, NewCreditSvc(tdb), null, new SubscriptionService(tdb.Db));
        var outcome = await svc.ApplyPaidWebhookAsync(111, "txn-1", "{}");

        Assert.Equal(WebhookApplyOutcome.SubscriptionActivated, outcome);

        var sub = Assert.Single(await tdb.Db.Subscriptions.ToListAsync());
        Assert.Equal(SubscriptionStatus.Active, sub.Status);
        Assert.Equal(order.Id, sub.OrderId);
        Assert.Equal(BillingCycle.Monthly, sub.BillingCycle);
        Assert.True(sub.ExpiresAt > DateTime.UtcNow.AddDays(29));

        // KHÔNG một bút toán credit nào phát sinh từ đơn thuê bao.
        Assert.Empty(await tdb.Db.CreditTransactions.Where(t => t.OrderId == order.Id).ToListAsync());
        Assert.Empty(await tdb.Db.CreditTransactions
            .Where(t => t.Reason == CreditTransactionReason.Purchase).ToListAsync());
    }

    // Hồi quy trực tiếp cho hình dạng lỗi DB20: khách đã trả tiền thì đơn PHẢI rời khỏi Pending.
    [Fact]
    public async Task MuaGoiThueBao_DonKhongBaoGioKetPending()
    {
        using var tdb = new PaymentTestDb();
        var owner = Guid.NewGuid();
        var order = await SeedPendingOrderAsync(
            tdb, OwnerType.Org, owner, NewSubPackage(365), OrderKind.SubscriptionPurchase, 222);

        var svc = new WebhookService(tdb.Db, NewCreditSvc(tdb), null, new SubscriptionService(tdb.Db));
        await svc.ApplyPaidWebhookAsync(222, "txn-2", "{}");

        var reloaded = await tdb.Db.Orders.AsNoTracking().FirstAsync(o => o.Id == order.Id);
        Assert.Equal(OrderStatus.Paid, reloaded.Status);
        Assert.NotNull(reloaded.PaidAt);

        // Gói 365 ngày → chu kỳ Năm (cột báo cáo).
        Assert.Equal(BillingCycle.Annual, (await tdb.Db.Subscriptions.FirstAsync()).BillingCycle);
    }

    // PAY-8 idempotent: PayOS redeliver KHÔNG được tặng thêm một kỳ hạn.
    [Fact]
    public async Task WebhookRedeliver_KhongCongThemKyHan()
    {
        using var tdb = new PaymentTestDb();
        await SeedPendingOrderAsync(
            tdb, OwnerType.User, Guid.NewGuid(), NewSubPackage(30), OrderKind.SubscriptionPurchase, 333);

        var svc = new WebhookService(tdb.Db, NewCreditSvc(tdb), null, new SubscriptionService(tdb.Db));
        Assert.Equal(WebhookApplyOutcome.SubscriptionActivated, await svc.ApplyPaidWebhookAsync(333, "a", "{}"));
        Assert.Equal(WebhookApplyOutcome.AlreadyProcessed, await svc.ApplyPaidWebhookAsync(333, "a", "{}"));

        Assert.Single(await tdb.Db.Subscriptions.ToListAsync());
    }

    // Gia hạn khi còn hạn: nối tiếp ngày hết hạn cũ, không cắt từ "bây giờ" (không mất ngày đã trả tiền).
    [Fact]
    public async Task GiaHan_NoiTiepHanCu_KhongCatTuHomNay()
    {
        using var tdb = new PaymentTestDb();
        var owner = Guid.NewGuid();
        await SeedWalletAsync(tdb, OwnerType.User, owner);

        var pkg = NewSubPackage(30);
        // FK subscriptions.order_id → orders: phải là đơn CÓ THẬT (chính cái ràng buộc giữ cho không ai
        // cấy được kỳ hạn không gắn với một lần trả tiền nào).
        var o1 = await SeedPendingOrderAsync(tdb, OwnerType.User, owner, pkg, OrderKind.SubscriptionPurchase, 901);
        var o2 = new Order
        {
            Id = Guid.NewGuid(),
            OwnerType = OwnerType.User,
            OwnerId = owner,
            Kind = OrderKind.SubscriptionRenewal,
            PackageId = pkg.Id,
            Status = OrderStatus.Pending,
            AmountVnd = pkg.PriceVnd,
            PayosOrderCode = 902,
            ExpiredAt = DateTime.UtcNow.AddMinutes(30),
            CreatedAt = DateTime.UtcNow
        };
        tdb.Db.Orders.Add(o2);
        await tdb.Db.SaveChangesAsync();

        var svc = new SubscriptionService(tdb.Db);

        var first = await svc.ActivateAsync(OwnerType.User, owner, o1.Id, pkg);
        await tdb.Db.SaveChangesAsync();
        Assert.NotNull(first);

        var second = await svc.ActivateAsync(OwnerType.User, owner, o2.Id, pkg);
        await tdb.Db.SaveChangesAsync();
        Assert.NotNull(second);

        Assert.Equal(first!.ExpiresAt, second!.StartedAt);
        Assert.True(second.ExpiresAt > first.ExpiresAt.AddDays(29));
    }

    // ─────────────────── gate unlimited ở đường reserve ───────────────────

    // Tiêu chí "tạo buổi khi ví 0 credit → vẫn chạy" trong ô F8.
    [Fact]
    public async Task Reserve_CoThueBao_ViRong_VanChay_VaKhongDungToiSoDu()
    {
        using var tdb = new PaymentTestDb();
        var owner = Guid.NewGuid();
        await SeedWalletAsync(tdb, OwnerType.User, owner, remaining: 0);
        await SeedSubscriptionAsync(tdb, OwnerType.User, owner, DateTime.UtcNow.AddDays(10));

        var result = await NewCreditSvc(tdb).ReserveAsync(OwnerType.User, owner, Guid.NewGuid());

        Assert.Equal(ReserveOutcome.Reserved, result.Outcome);

        var res = Assert.Single(await tdb.Db.CreditReservations.AsNoTracking().ToListAsync());
        Assert.Equal(ReservationFunding.Subscription, res.FundedBy);

        // Bất biến (1): cả hai cột số dư đứng yên, sổ cái trống ⇒ vế trái = vế phải = 0.
        var acc = await tdb.Db.CreditAccounts.AsNoTracking().FirstAsync();
        Assert.Equal(0, acc.RemainingCredits);
        Assert.Equal(0, acc.ReservedCredits);
        Assert.Empty(await tdb.Db.CreditTransactions.ToListAsync());
    }

    // Tiêu chí "sub hết hạn → về đúng luật credit" — vế 402.
    [Fact]
    public async Task Reserve_ThueBaoHetHan_ViRong_VeDungLuatCredit_402()
    {
        using var tdb = new PaymentTestDb();
        var owner = Guid.NewGuid();
        await SeedWalletAsync(tdb, OwnerType.User, owner, remaining: 0);
        await SeedSubscriptionAsync(tdb, OwnerType.User, owner, DateTime.UtcNow.AddDays(-1));

        var result = await NewCreditSvc(tdb).ReserveAsync(OwnerType.User, owner, Guid.NewGuid());

        Assert.Equal(ReserveOutcome.Insufficient, result.Outcome);
        Assert.Empty(await tdb.Db.CreditReservations.ToListAsync());   // PAY-5: không để lại chỗ giữ dư
    }

    // Vế còn lại: hết hạn nhưng CÓ credit → trừ ví bình thường, funded_by='Credit'.
    [Fact]
    public async Task Reserve_ThueBaoHetHan_ConCredit_TruViNhuCu()
    {
        using var tdb = new PaymentTestDb();
        var owner = Guid.NewGuid();
        await SeedWalletAsync(tdb, OwnerType.User, owner, remaining: 1);
        await SeedSubscriptionAsync(tdb, OwnerType.User, owner, DateTime.UtcNow.AddDays(-1));

        var result = await NewCreditSvc(tdb).ReserveAsync(OwnerType.User, owner, Guid.NewGuid());

        Assert.Equal(ReserveOutcome.Reserved, result.Outcome);
        Assert.Equal(ReservationFunding.Credit,
            (await tdb.Db.CreditReservations.AsNoTracking().FirstAsync()).FundedBy);

        var acc = await tdb.Db.CreditAccounts.AsNoTracking().FirstAsync();
        Assert.Equal(0, acc.RemainingCredits);
        Assert.Equal(1, acc.ReservedCredits);
    }

    // Thuê bao bị HUỶ giữa kỳ (hoàn tiền) → chặn ngay, không đợi tới expires_at.
    [Fact]
    public async Task Reserve_ThueBaoDaHuy_KhongMoKhoa()
    {
        using var tdb = new PaymentTestDb();
        var owner = Guid.NewGuid();
        await SeedWalletAsync(tdb, OwnerType.User, owner, remaining: 0);
        await SeedSubscriptionAsync(
            tdb, OwnerType.User, owner, DateTime.UtcNow.AddDays(10), SubscriptionStatus.Cancelled);

        var result = await NewCreditSvc(tdb).ReserveAsync(OwnerType.User, owner, Guid.NewGuid());

        Assert.Equal(ReserveOutcome.Insufficient, result.Outcome);
    }

    // PAY-12: ví bị Đình chỉ → thuê bao KHÔNG mua được quyền đi vòng qua lệnh đình chỉ.
    [Fact]
    public async Task Reserve_ViSuspended_DuCoThueBao_Van402()
    {
        using var tdb = new PaymentTestDb();
        var owner = Guid.NewGuid();
        await SeedWalletAsync(tdb, OwnerType.User, owner, remaining: 0, status: CreditAccountStatus.Suspended);
        await SeedSubscriptionAsync(tdb, OwnerType.User, owner, DateTime.UtcNow.AddDays(10));

        var result = await NewCreditSvc(tdb).ReserveAsync(OwnerType.User, owner, Guid.NewGuid());

        Assert.Equal(ReserveOutcome.Insufficient, result.Outcome);
        Assert.Empty(await tdb.Db.CreditReservations.ToListAsync());
    }

    // Thuê bao của Org (membership B2B) mở khoá cho chính ví Org.
    [Fact]
    public async Task Reserve_ThueBaoOrg_MoKhoaViOrg()
    {
        using var tdb = new PaymentTestDb();
        var org = Guid.NewGuid();
        await SeedWalletAsync(tdb, OwnerType.Org, org, remaining: 0);
        await SeedSubscriptionAsync(tdb, OwnerType.Org, org, DateTime.UtcNow.AddDays(10));

        var result = await NewCreditSvc(tdb).ReserveAsync(OwnerType.Org, org, Guid.NewGuid());

        Assert.Equal(ReserveOutcome.Reserved, result.Outcome);
    }

    // ─────────────────── consume / release ───────────────────

    [Fact]
    public async Task Consume_ChoGiuThueBao_KhongGhiSoCai_KhongDoiSoDu()
    {
        using var tdb = new PaymentTestDb();
        var owner = Guid.NewGuid();
        var session = Guid.NewGuid();
        await SeedWalletAsync(tdb, OwnerType.User, owner, remaining: 0);
        await SeedSubscriptionAsync(tdb, OwnerType.User, owner, DateTime.UtcNow.AddDays(10));

        var svc = NewCreditSvc(tdb);
        await svc.ReserveAsync(OwnerType.User, owner, session);
        var consumed = await svc.ConsumeAsync(session);

        Assert.Equal(ConsumeOutcome.Consumed, consumed.Outcome);

        var res = await tdb.Db.CreditReservations.AsNoTracking().FirstAsync();
        Assert.Equal(ReservationStatus.Consumed, res.Status);   // vết tiêu thụ vẫn còn ở reservation

        var acc = await tdb.Db.CreditAccounts.AsNoTracking().FirstAsync();
        Assert.Equal(0, acc.RemainingCredits);
        Assert.Equal(0, acc.ReservedCredits);
        Assert.Empty(await tdb.Db.CreditTransactions.ToListAsync());
    }

    // 🔴 Ca nguy hiểm nhất cả tính năng: nếu release chạy nhánh prepaid thì remaining 0 → 1, tức là
    // ĐÚC RA một credit trả tiền chưa từng được mua, mỗi lần subscriber bỏ ngang buổi thi.
    [Fact]
    public async Task Release_ChoGiuThueBao_KhongDucRaCreditTuHuKhong()
    {
        using var tdb = new PaymentTestDb();
        var owner = Guid.NewGuid();
        var session = Guid.NewGuid();
        await SeedWalletAsync(tdb, OwnerType.User, owner, remaining: 0);
        await SeedSubscriptionAsync(tdb, OwnerType.User, owner, DateTime.UtcNow.AddDays(10));

        var svc = NewCreditSvc(tdb);
        await svc.ReserveAsync(OwnerType.User, owner, session);
        var released = await svc.ReleaseAsync(session);

        Assert.Equal(ReleaseOutcome.Released, released.Outcome);

        var acc = await tdb.Db.CreditAccounts.AsNoTracking().FirstAsync();
        Assert.Equal(0, acc.RemainingCredits);   // ← vế quan trọng
        Assert.Equal(0, acc.ReservedCredits);
        Assert.Empty(await tdb.Db.CreditTransactions.ToListAsync());
    }

    // 🔬 Test này sinh ra TỪ một mutation-check VẪN XANH, không phải từ suy diễn.
    // Bỏ nhánh sớm trong ReleaseAsync mà test ở trên vẫn xanh — vì chỗ giữ thuê bao không cộng
    // reserved_credits, nên câu UPDATE prepaid có guard DB22 `reserved >= 1` khớp 0 row và tình cờ không
    // đúc credit. Sự che chắn đó CHỈ đúng khi ví không còn chỗ giữ credit nào khác.
    //
    // Ca thật làm sập nó: người dùng bắt đầu buổi A bằng credit (reserved=1), rồi NÂNG CẤP lên thuê bao
    // giữa chừng, rồi bắt đầu buổi B bằng thuê bao và bỏ ngang. Lúc đó `reserved >= 1` khớp ⇒ nhánh
    // prepaid chạy ⇒ vừa ĐÚC ra 1 credit trả tiền từ hư không, vừa xoá mất chỗ giữ của buổi A.
    [Fact]
    public async Task Release_ChoGiuThueBao_KhiViVanConChoGiuCredit_VanKhongDucCredit()
    {
        using var tdb = new PaymentTestDb();
        var owner = Guid.NewGuid();
        var sessionCredit = Guid.NewGuid();
        var sessionSub = Guid.NewGuid();
        await SeedWalletAsync(tdb, OwnerType.User, owner, remaining: 1);

        var svc = NewCreditSvc(tdb);

        // Buổi A: trả bằng credit (chưa có thuê bao) → remaining 1→0, reserved 0→1.
        await svc.ReserveAsync(OwnerType.User, owner, sessionCredit);

        // ... nâng cấp lên thuê bao giữa chừng.
        await SeedSubscriptionAsync(tdb, OwnerType.User, owner, DateTime.UtcNow.AddDays(10));

        // Buổi B: thuê bao tài trợ → không đụng cột nào.
        await svc.ReserveAsync(OwnerType.User, owner, sessionSub);
        await svc.ReleaseAsync(sessionSub);

        var acc = await tdb.Db.CreditAccounts.AsNoTracking().FirstAsync();
        Assert.Equal(0, acc.RemainingCredits);   // KHÔNG đúc credit
        Assert.Equal(1, acc.ReservedCredits);    // chỗ giữ của buổi A còn nguyên
    }

    // "Hết hạn giữa buổi thi thì sao" (câu hỏi ô F8 bắt trả lời): người đang thi KHÔNG bị đụng tới, và
    // nghịch đảo vẫn khớp chiều thuận vì nhánh chọn theo funded_by đã ghi cứng, không theo trạng thái
    // thuê bao HIỆN TẠI. Bỏ điều đó đi thì đúng ca này sẽ đúc credit.
    [Fact]
    public async Task ThueBaoHetHanGiuaBuoi_ReleaseVanKhongDucCredit()
    {
        using var tdb = new PaymentTestDb();
        var owner = Guid.NewGuid();
        var session = Guid.NewGuid();
        await SeedWalletAsync(tdb, OwnerType.User, owner, remaining: 0);
        await SeedSubscriptionAsync(tdb, OwnerType.User, owner, DateTime.UtcNow.AddDays(10));

        var svc = NewCreditSvc(tdb);
        await svc.ReserveAsync(OwnerType.User, owner, session);

        // ... thuê bao hết hạn NGAY GIỮA buổi thi (và sweeper cũng đã đóng dấu Expired).
        await tdb.Db.Subscriptions.ExecuteUpdateAsync(u => u
            .SetProperty(s => s.ExpiresAt, DateTime.UtcNow.AddSeconds(-1))
            .SetProperty(s => s.Status, SubscriptionStatus.Expired));

        await svc.ReleaseAsync(session);

        var acc = await tdb.Db.CreditAccounts.AsNoTracking().FirstAsync();
        Assert.Equal(0, acc.RemainingCredits);
        Assert.Equal(0, acc.ReservedCredits);
    }

    [Fact]
    public async Task ThueBaoHetHanGiuaBuoi_ConsumeVanKhongTruVi()
    {
        using var tdb = new PaymentTestDb();
        var owner = Guid.NewGuid();
        var session = Guid.NewGuid();
        await SeedWalletAsync(tdb, OwnerType.User, owner, remaining: 5);
        await SeedSubscriptionAsync(tdb, OwnerType.User, owner, DateTime.UtcNow.AddDays(10));

        var svc = NewCreditSvc(tdb);
        await svc.ReserveAsync(OwnerType.User, owner, session);

        await tdb.Db.Subscriptions.ExecuteUpdateAsync(u => u
            .SetProperty(s => s.ExpiresAt, DateTime.UtcNow.AddSeconds(-1)));

        await svc.ConsumeAsync(session);

        // Buổi đã bắt đầu dưới thuê bao thì tiêu thụ của nó vẫn thuộc thuê bao — không quay sang bổ vào
        // credit đã mua của người dùng.
        var acc = await tdb.Db.CreditAccounts.AsNoTracking().FirstAsync();
        Assert.Equal(5, acc.RemainingCredits);
        Assert.Empty(await tdb.Db.CreditTransactions.ToListAsync());
    }

    // ─────────────────── bất biến (2): reconciler ───────────────────

    // Nếu reconciler đếm cả chỗ giữ của subscriber, nó sẽ "sửa" reserved_credits 0 → 1 và phá bất biến
    // (1) — đúng lớp bug DB21, chỉ khác cửa vào.
    [Fact]
    public async Task Reconciler_KhongDemChoGiuDoThueBaoTaiTro()
    {
        using var tdb = new PaymentTestDb();
        var owner = Guid.NewGuid();
        await SeedWalletAsync(tdb, OwnerType.User, owner, remaining: 0);
        await SeedSubscriptionAsync(tdb, OwnerType.User, owner, DateTime.UtcNow.AddDays(10));
        await NewCreditSvc(tdb).ReserveAsync(OwnerType.User, owner, Guid.NewGuid());

        await RunReconcilerOnceAsync(tdb);

        var acc = await tdb.Db.CreditAccounts.AsNoTracking().FirstAsync();
        Assert.Equal(0, acc.ReservedCredits);
    }

    // Đối chứng: chỗ giữ do credit tài trợ thì reconciler VẪN phải sửa drift như trước (không vô hiệu
    // hoá DB4 bằng cách thêm vế funded_by).
    [Fact]
    public async Task Reconciler_VanSuaDriftChoChoGiuCredit()
    {
        using var tdb = new PaymentTestDb();
        var owner = Guid.NewGuid();
        await SeedWalletAsync(tdb, OwnerType.User, owner, remaining: 1);
        await NewCreditSvc(tdb).ReserveAsync(OwnerType.User, owner, Guid.NewGuid());

        // Bơm drift: reserved_credits bị lệch khỏi count thật.
        await tdb.Db.CreditAccounts.ExecuteUpdateAsync(u => u.SetProperty(a => a.ReservedCredits, 7));

        await RunReconcilerOnceAsync(tdb);

        var acc = await tdb.Db.CreditAccounts.AsNoTracking().FirstAsync();
        Assert.Equal(1, acc.ReservedCredits);
    }

    private static async Task RunReconcilerOnceAsync(PaymentTestDb tdb)
    {
        var services = new ServiceCollection();
        services.AddDbContext<PaymentDbContext>(o => o
            .UseSqlite(tdb.Connection)
            .UseSnakeCaseNamingConvention());
        var provider = services.BuildServiceProvider();

        var reconciler = new CreditReservationReconciler(
            provider.GetRequiredService<IServiceScopeFactory>(),
            Options.Create(new ReconcileSettings { Enabled = true, ScanIntervalSeconds = 120 }),
            NullLogger<CreditReservationReconciler>.Instance);

        var scan = typeof(CreditReservationReconciler)
            .GetMethod("ScanOnceAsync", BindingFlags.NonPublic | BindingFlags.Instance)!;
        await (Task)scan.Invoke(reconciler, [CancellationToken.None])!;
    }

    // ─────────────────── hết hạn / báo cáo ───────────────────

    [Fact]
    public async Task ExpireDue_DongDauQuaHan_KhongDungKyConHan()
    {
        using var tdb = new PaymentTestDb();
        var a = Guid.NewGuid();
        var b = Guid.NewGuid();
        await SeedWalletAsync(tdb, OwnerType.User, a);
        await SeedWalletAsync(tdb, OwnerType.User, b);
        await SeedSubscriptionAsync(tdb, OwnerType.User, a, DateTime.UtcNow.AddDays(-2));
        await SeedSubscriptionAsync(tdb, OwnerType.User, b, DateTime.UtcNow.AddDays(5));

        var closed = await new SubscriptionService(tdb.Db).ExpireDueAsync();

        Assert.Equal(1, closed);
        Assert.Equal(SubscriptionStatus.Expired,
            (await tdb.Db.Subscriptions.AsNoTracking().FirstAsync(s => s.OwnerId == a)).Status);
        Assert.Equal(SubscriptionStatus.Active,
            (await tdb.Db.Subscriptions.AsNoTracking().FirstAsync(s => s.OwnerId == b)).Status);
    }

    // Luật vào bài KHÔNG phụ thuộc sweeper: kỳ quá hạn mà chưa kịp đóng dấu vẫn phải bị coi là hết hạn.
    [Fact]
    public async Task HasActive_KhongPhuThuocSweeperDongDau()
    {
        using var tdb = new PaymentTestDb();
        var owner = Guid.NewGuid();
        await SeedWalletAsync(tdb, OwnerType.User, owner);
        await SeedSubscriptionAsync(tdb, OwnerType.User, owner, DateTime.UtcNow.AddDays(-1));   // vẫn status=Active

        Assert.False(await new SubscriptionService(tdb.Db).HasActiveAsync(OwnerType.User, owner));
    }

    // ─────────────────── hồi quy đường credit ───────────────────

    // Đường mua credit pack KHÔNG được đổi hành vi vì F8.
    [Fact]
    public async Task HoiQuy_MuaCreditPack_VanCongCreditVaGhiSoCai()
    {
        using var tdb = new PaymentTestDb();
        var owner = Guid.NewGuid();
        var pkg = new ProductPackage
        {
            Id = Guid.NewGuid(),
            Name = "Pack 5",
            Type = PackageType.OneTime,
            PriceVnd = 100_000,
            InterviewCredits = 5,
            IsActive = true,
            CreatedAt = DateTime.UtcNow
        };
        await SeedPendingOrderAsync(tdb, OwnerType.User, owner, pkg, OrderKind.CreditPack, 444);

        var svc = new WebhookService(tdb.Db, NewCreditSvc(tdb), null, new SubscriptionService(tdb.Db));
        Assert.Equal(WebhookApplyOutcome.Credited, await svc.ApplyPaidWebhookAsync(444, "t", "{}"));

        var acc = await tdb.Db.CreditAccounts.AsNoTracking().FirstAsync();
        Assert.Equal(5, acc.RemainingCredits);
        Assert.Equal(5, await tdb.Db.CreditTransactions.SumAsync(t => t.Delta));
    }

    // Không có thuê bao ⇒ hành vi reserve y hệt trước F8 (funded_by='Credit', trừ ví).
    [Fact]
    public async Task HoiQuy_KhongCoThueBao_ReserveTruViNhuCu()
    {
        using var tdb = new PaymentTestDb();
        var owner = Guid.NewGuid();
        await SeedWalletAsync(tdb, OwnerType.User, owner, remaining: 2);

        await NewCreditSvc(tdb).ReserveAsync(OwnerType.User, owner, Guid.NewGuid());

        var acc = await tdb.Db.CreditAccounts.AsNoTracking().FirstAsync();
        Assert.Equal(1, acc.RemainingCredits);
        Assert.Equal(1, acc.ReservedCredits);
        Assert.Equal(ReservationFunding.Credit,
            (await tdb.Db.CreditReservations.AsNoTracking().FirstAsync()).FundedBy);
    }
}
