using System.Runtime.CompilerServices;
using Isas.PaymentService.Models;
using Isas.PaymentService.Services;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Diagnostics;
using Microsoft.EntityFrameworkCore.Storage;
using Microsoft.Extensions.Options;
using Moq;
using PaymentService.Models;

namespace Isas.PaymentService.Tests;

/// <summary>
/// DB25b — hàng rào cho một lớp lỗi CHỈ NỔ TRÊN POSTGRES, trên 7 site transaction của Payment.
///
/// <para><b>Vế 1 — "phải bọc":</b> <c>Program.cs</c> bật <c>EnableRetryOnFailure()</c>, mà chiến lược
/// retry của EF từ chối transaction do người dùng tự mở. SQLite mặc định chạy chiến lược KHÔNG-retry
/// nên toàn bộ suite hiện tại MÙ với ràng buộc này — đúng kiểu "CI xanh, production 500 mọi request".</para>
///
/// <para><b>Vế 2 — "bọc thôi chưa đủ":</b> retry chạy lại delegate nhưng EF KHÔNG reset change tracker.
/// Bút toán <c>Add()</c> bên trong transaction sẽ bị chèn HAI LẦN ở lần thử sau, trong khi số dư (làm
/// bằng <c>ExecuteUpdate</c>) chỉ đổi MỘT lần ⇒ gãy bất biến <c>remaining + reserved = Σ delta</c>.
/// Các test "retry THẬT" ép hỏng đúng một lần rồi đếm dòng sổ cái thật trong DB.</para>
/// </summary>
public class ExecutionStrategyDb25bTests
{
    private static readonly Guid Admin = Guid.NewGuid();

    // ── Vế 1: cả 7 site chạy được dưới chiến lược CÓ retry ────────────────

    [Fact]
    public async Task Reserve_ChayDuocDuoiChienLuocCoRetry()
    {
        using var tdb = new PaymentTestDb();
        var owner = Guid.NewGuid();
        await SeedWalletAsync(tdb, OwnerType.User, owner, remaining: 3);

        using var ctx = Retryable(tdb);
        var res = await new CreditAccountService(ctx).ReserveAsync(OwnerType.User, owner, Guid.NewGuid());

        Assert.Equal(ReserveOutcome.Reserved, res.Outcome);
    }

    [Fact]
    public async Task Consume_ChayDuocDuoiChienLuocCoRetry()
    {
        using var tdb = new PaymentTestDb();
        var (_, session) = await SeedReservedAsync(tdb);

        using var ctx = Retryable(tdb);
        var res = await new CreditAccountService(ctx).ConsumeAsync(session);

        Assert.Equal(ConsumeOutcome.Consumed, res.Outcome);
    }

    [Fact]
    public async Task Release_ChayDuocDuoiChienLuocCoRetry()
    {
        using var tdb = new PaymentTestDb();
        var (_, session) = await SeedReservedAsync(tdb);

        using var ctx = Retryable(tdb);
        var res = await new CreditAccountService(ctx).ReleaseAsync(session);

        Assert.Equal(ReleaseOutcome.Released, res.Outcome);
    }

    [Fact]
    public async Task Webhook_ChayDuocDuoiChienLuocCoRetry()
    {
        using var tdb = new PaymentTestDb();
        const long code = 260807000001;
        await SeedPendingOrderAsync(tdb, code, credits: 5);

        using var ctx = Retryable(tdb);
        var res = await new WebhookService(ctx, new CreditAccountService(ctx))
            .ApplyPaidWebhookAsync(code, "txn-1", "{}");

        Assert.Equal(WebhookApplyOutcome.Credited, res);
    }

    [Fact]
    public async Task CloseBillingPeriod_ChayDuocDuoiChienLuocCoRetry()
    {
        using var tdb = new PaymentTestDb();
        var org = Guid.NewGuid();
        await SeedPostpaidWalletAsync(tdb, org, periodUsage: 4);

        using var ctx = Retryable(tdb);
        var res = await new InvoiceService(
                ctx, new Mock<IOrderService>().Object,
                Options.Create(new BillingSettings { UnitPrice = 50_000m }))
            .CloseBillingPeriodAsync(org);

        Assert.Equal(IInvoiceService.CloseBillingPeriodOutcome.Closed, res.Outcome);
    }

    [Fact]
    public async Task Grant_ChayDuocDuoiChienLuocCoRetry()
    {
        using var tdb = new PaymentTestDb();
        var owner = Guid.NewGuid();
        await SeedWalletAsync(tdb, OwnerType.User, owner, remaining: 1);

        using var ctx = Retryable(tdb);
        var res = await new AdminCreditService(ctx, new CreditAccountService(ctx))
            .GrantAsync(OwnerType.User, owner, 5, "quà", null, Admin);

        Assert.Equal(GrantOutcome.Granted, res.Outcome);
    }

    [Fact]
    public async Task Refund_ChayDuocDuoiChienLuocCoRetry()
    {
        using var tdb = new PaymentTestDb();
        var order = await SeedPaidOrderWithPurchaseAsync(tdb, credits: 5);

        using var ctx = Retryable(tdb);
        var res = await new RefundService(ctx)
            .RefundOrderAsync(order, Admin, "lý do", null, allowPartialClawback: false);

        Assert.Equal(RefundOutcome.Refunded, res.Outcome);
    }

    // ── Vế 2: retry THẬT không được nhân đôi bút toán ─────────────────────

    /// <summary>
    /// Đường tiêu credit. Ép hỏng đúng câu INSERT sổ cái, đúng một lần → strategy chạy lại delegate.
    /// Nếu delegate không dọn change tracker, <c>CreditTransaction</c> của lần thử đầu còn kẹt ở
    /// <c>Added</c> ⇒ lần thử sau chèn CẢ HAI ⇒ ví bị trừ 1 mà sổ cái ghi −2.
    /// </summary>
    [Fact]
    public async Task Consume_RetryThat_ChiGhiDungMotButToan()
    {
        using var tdb = new PaymentTestDb();
        var (owner, session) = await SeedReservedAsync(tdb);
        var fault = new ThrowOnceInterceptor("credit_transactions");

        using var ctx = Retryable(tdb, real: true, interceptor: fault);
        var res = await new CreditAccountService(ctx).ConsumeAsync(session);

        Assert.True(fault.Fired, "Interceptor chưa hề kích hoạt ⇒ phép thử không chứng minh được gì.");
        Assert.Equal(ConsumeOutcome.Consumed, res.Outcome);
        await AssertLedgerBalancedAsync(tdb, OwnerType.User, owner, expectedRows: 2); // Purchase + Consume
    }

    /// <summary>
    /// Đường cộng credit (khách vừa trả tiền thật). Cùng lý lẽ, ngược dấu.
    ///
    /// ⚠ Ví được seed sẵn CÓ CHỦ ĐÍCH: nếu để ví chưa tồn tại thì <c>CreateAccountAsync</c> chạy trong
    /// transaction và tự ghi bút toán <c>FreeGrant</c> (F7) — sự cố giả sẽ rơi vào ĐÓ chứ không vào
    /// bút toán <c>Purchase</c> mà test này muốn nhắm, rồi bị nuốt bởi <c>catch (DbUpdateException)</c>
    /// sẵn có ở <c>WebhookService</c>. Muốn đo cái gì thì phải bắn trúng cái đó.
    /// </summary>
    [Fact]
    public async Task Webhook_RetryThat_ChiGhiDungMotButToan()
    {
        using var tdb = new PaymentTestDb();
        const long code = 260807000002;
        var owner = await SeedPendingOrderAsync(tdb, code, credits: 5);
        await SeedWalletAsync(tdb, OwnerType.User, owner, remaining: 0);
        var fault = new ThrowOnceInterceptor("credit_transactions");

        using var ctx = Retryable(tdb, real: true, interceptor: fault);
        var res = await new WebhookService(ctx, new CreditAccountService(ctx))
            .ApplyPaidWebhookAsync(code, "txn-1", "{}");

        Assert.True(fault.Fired, "Interceptor chưa hề kích hoạt ⇒ phép thử không chứng minh được gì.");
        Assert.Equal(WebhookApplyOutcome.Credited, res);
        await AssertLedgerBalancedAsync(tdb, OwnerType.User, owner, expectedRows: 1); // chỉ Purchase
    }

    // ── Vế 3: guard cấu trúc cho MỌI site về sau ──────────────────────────

    /// <summary>
    /// Bảy test hành vi ở trên chỉ phủ bảy site đang có. Guard này đọc thẳng mã nguồn để bắt site
    /// MỚI: bất kỳ <c>BeginTransactionAsync</c> nào cũng phải nằm trong một khối
    /// <c>DbRetry.RunAsync</c>. Không có nó, người thêm transaction thứ tám sẽ ship một
    /// <c>InvalidOperationException</c> ở mọi request Postgres mà CI vẫn xanh.
    /// </summary>
    [Fact]
    public void MoiTransactionTuMo_DeuNamTrongDbRetry()
    {
        var offenders = TransactionSiteScanner.FindUnwrapped(
            Path.Combine(RepoRoot(), "src", "services", "Isas.PaymentService"));

        Assert.True(offenders.Count == 0,
            "BeginTransactionAsync KHÔNG nằm trong DbRetry.RunAsync (sẽ ném trên Postgres):\n  "
            + string.Join("\n  ", offenders));
    }

    /// <summary>
    /// ĐỐI CHỨNG DƯƠNG cho guard ở trên. Một luật quét mã nguồn có thể "sạch" chỉ vì nó ĐÃ CHẾT
    /// (regex trượt, thư mục sai) — lúc đó nó im lặng đúng bằng lúc code sạch. Test này ép scanner
    /// nhìn một site cố tình KHÔNG bọc (phải bắt) và một site có bọc (không được báo nhầm).
    /// </summary>
    [Fact]
    public void Scanner_BatSiteKhongBoc_BoQuaSiteDaBoc()
    {
        var dir = Path.Combine(Path.GetTempPath(), "db25b-scan-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(dir);
        try
        {
            File.WriteAllLines(Path.Combine(dir, "Offender.cs"), new[]
            {
                "class Offender {",
                "    async Task X(CancellationToken ct) {",
                "        await using var tx = await _db.Database.BeginTransactionAsync(ct);",
                "        await tx.CommitAsync(ct);",
                "    }",
                "}",
            });
            File.WriteAllLines(Path.Combine(dir, "Good.cs"), new[]
            {
                "class Good {",
                "    async Task<int> X(CancellationToken ct) =>",
                "        await DbRetry.RunAsync(_db, async Task<int> () =>",
                "        {",
                "            await using var tx = await _db.Database.BeginTransactionAsync(ct);",
                "            await tx.CommitAsync(ct);",
                "            return 1;",
                "        });",
                "}",
            });

            var found = TransactionSiteScanner.FindUnwrapped(dir);

            Assert.Single(found);
            Assert.Contains("Offender.cs", found[0]);
        }
        finally
        {
            Directory.Delete(dir, recursive: true);
        }
    }

    /// <summary>
    /// Nửa còn lại của DB25b: bọc transaction mà KHÔNG bật retry thì cả task này vô nghĩa. Không test
    /// nào khác chạm tới <c>Program.cs</c> (suite chạy SQLite, tự dựng DbContext), nên gỡ
    /// <c>EnableRetryOnFailure</c> sẽ trôi qua toàn bộ CI trong im lặng. Đọc thẳng mã nguồn là cách
    /// duy nhất khoá được nó ở tầng unit test.
    /// </summary>
    [Fact]
    public void ProgramCs_BatEnableRetryOnFailure()
    {
        var program = File.ReadAllText(
            Path.Combine(RepoRoot(), "src", "services", "Isas.PaymentService", "Program.cs"));

        Assert.Contains("EnableRetryOnFailure", program);
    }

    // ── helpers ───────────────────────────────────────────────────────────

    /// <summary>DbContext SQLite gắn execution strategy: <paramref name="real"/>=false chỉ bật ràng
    /// buộc "cấm tự mở transaction"; =true thì thử lại THẬT khi gặp lỗi tạm thời giả.</summary>
    private static PaymentDbContext Retryable(
        PaymentTestDb tdb, bool real = false, IInterceptor? interceptor = null)
        => tdb.NewContext(
            real
                ? deps => new RetryOnTestFaultStrategy(deps)
                : deps => new RetryingStrategyStub(deps),
            interceptor is null ? null : new[] { interceptor });

    /// <summary>Bất biến tiền của repo: <c>remaining + reserved = Σ delta</c>, kèm số dòng sổ cái mong đợi
    /// (nhân đôi bút toán vẫn giữ tổng ĐÚNG ở một số ca, nên phải kiểm CẢ HAI).</summary>
    private static async Task AssertLedgerBalancedAsync(
        PaymentTestDb tdb, OwnerType ownerType, Guid ownerId, int expectedRows)
    {
        await using var db = tdb.NewContext();
        var rows = await db.CreditTransactions
            .Where(t => t.OwnerType == ownerType && t.OwnerId == ownerId).ToListAsync();
        var acc = await db.CreditAccounts
            .SingleAsync(a => a.OwnerType == ownerType && a.OwnerId == ownerId);

        Assert.Equal(expectedRows, rows.Count);
        Assert.Equal(rows.Sum(t => t.Delta), acc.RemainingCredits + acc.ReservedCredits);
    }

    private static async Task<CreditAccount> SeedWalletAsync(
        PaymentTestDb tdb, OwnerType ownerType, Guid ownerId, int remaining, int reserved = 0)
    {
        var acc = new CreditAccount
        {
            Id = Guid.NewGuid(),
            OwnerType = ownerType,
            OwnerId = ownerId,
            PaymentMode = PaymentMode.Prepaid,
            Status = CreditAccountStatus.Active,
            RemainingCredits = remaining,
            ReservedCredits = reserved,
            UpdatedAt = DateTime.UtcNow,
        };
        tdb.Db.CreditAccounts.Add(acc);
        if (remaining + reserved > 0)
            tdb.Db.CreditTransactions.Add(new CreditTransaction
            {
                Id = Guid.NewGuid(),
                OwnerType = ownerType,
                OwnerId = ownerId,
                Delta = remaining + reserved,
                Reason = CreditTransactionReason.Purchase,
                CreatedAt = DateTime.UtcNow.AddMinutes(-5),
            });
        await tdb.Db.SaveChangesAsync();
        return acc;
    }

    private static async Task SeedPostpaidWalletAsync(PaymentTestDb tdb, Guid orgId, int periodUsage)
    {
        tdb.Db.CreditAccounts.Add(new CreditAccount
        {
            Id = Guid.NewGuid(),
            OwnerType = OwnerType.Org,
            OwnerId = orgId,
            PaymentMode = PaymentMode.Postpaid,
            Status = CreditAccountStatus.Active,
            RemainingCredits = 0,
            ReservedCredits = 0,
            CreditLimit = 100,
            PeriodUsage = periodUsage,
            UpdatedAt = DateTime.UtcNow,
        });
        await tdb.Db.SaveChangesAsync();
    }

    /// <summary>Ví có 1 credit ĐANG GIỮ (reserved) + reservation Reserved cho một buổi.</summary>
    private static async Task<(Guid Owner, Guid Session)> SeedReservedAsync(PaymentTestDb tdb)
    {
        var owner = Guid.NewGuid();
        var session = Guid.NewGuid();
        await SeedWalletAsync(tdb, OwnerType.User, owner, remaining: 0, reserved: 1);
        tdb.Db.CreditReservations.Add(new CreditReservation
        {
            Id = Guid.NewGuid(),
            OwnerType = OwnerType.User,
            OwnerId = owner,
            SessionId = session,
            Status = ReservationStatus.Reserved,
            FundedBy = ReservationFunding.Credit,
            PaymentMode = PaymentMode.Prepaid,
            CreatedAt = DateTime.UtcNow,
        });
        await tdb.Db.SaveChangesAsync();
        return (owner, session);
    }

    /// <summary>Đơn Pending + gói sinh credit; ví CHƯA tồn tại (đúng đường "lần mua đầu").</summary>
    private static async Task<Guid> SeedPendingOrderAsync(PaymentTestDb tdb, long orderCode, int credits)
    {
        var owner = Guid.NewGuid();
        var pkg = new ProductPackage
        {
            Id = Guid.NewGuid(),
            Name = $"Pack {credits}",
            Type = PackageType.OneTime,
            PriceVnd = 100_000,
            InterviewCredits = credits,
            IsActive = true,
            CreatedAt = DateTime.UtcNow,
        };
        tdb.Db.ProductPackages.Add(pkg);
        tdb.Db.Orders.Add(new Order
        {
            Id = Guid.NewGuid(),
            OwnerType = OwnerType.User,
            OwnerId = owner,
            Kind = OrderKind.CreditPack,
            PackageId = pkg.Id,
            Status = OrderStatus.Pending,
            AmountVnd = 100_000,
            PayosOrderCode = orderCode,
            ExpiredAt = DateTime.UtcNow.AddMinutes(30),
            CreatedAt = DateTime.UtcNow,
        });
        await tdb.Db.SaveChangesAsync();
        return owner;
    }

    /// <summary>Đơn đã Paid + bút toán Purchase tương ứng + ví còn đủ credit để thu hồi.</summary>
    private static async Task<Guid> SeedPaidOrderWithPurchaseAsync(PaymentTestDb tdb, int credits)
    {
        var owner = Guid.NewGuid();
        var orderId = Guid.NewGuid();
        await SeedWalletAsync(tdb, OwnerType.User, owner, remaining: credits);

        tdb.Db.Orders.Add(new Order
        {
            Id = orderId,
            OwnerType = OwnerType.User,
            OwnerId = owner,
            Kind = OrderKind.CreditPack,
            Status = OrderStatus.Paid,
            AmountVnd = 100_000,
            PayosOrderCode = 260807000009,
            PaidAt = DateTime.UtcNow.AddMinutes(-1),
            CreatedAt = DateTime.UtcNow.AddMinutes(-2),
        });
        tdb.Db.CreditTransactions.Add(new CreditTransaction
        {
            Id = Guid.NewGuid(),
            OwnerType = OwnerType.User,
            OwnerId = owner,
            OrderId = orderId,
            Delta = credits,
            Reason = CreditTransactionReason.Purchase,
            CreatedAt = DateTime.UtcNow.AddMinutes(-1),
        });
        await tdb.Db.SaveChangesAsync();
        return orderId;
    }

    // Neo theo đường dẫn file NGUỒN lúc biên dịch — không phụ thuộc thư mục làm việc của test runner.
    private static string RepoRoot([CallerFilePath] string here = "")
        => Path.GetFullPath(Path.Combine(Path.GetDirectoryName(here)!, "..", "..", ".."));

    /// <summary>Chiến lược CÓ <c>RetriesOnFailure</c> nhưng không thử lại thật — chỉ để bật đúng ràng
    /// buộc "không cho tự mở transaction" của EF (mẫu <c>AccountCreationTransactionTests</c>).</summary>
    private sealed class RetryingStrategyStub(ExecutionStrategyDependencies dependencies)
        : ExecutionStrategy(dependencies, maxRetryCount: 1, maxRetryDelay: TimeSpan.Zero)
    {
        public override bool RetriesOnFailure => true;
        protected override bool ShouldRetryOn(Exception exception) => false;
    }
}
