using Isas.PaymentService.Services;
using Microsoft.EntityFrameworkCore;
using PaymentService.Models;

namespace Isas.PaymentService.Tests;

/// <summary>
/// Chỉ số tài chính kiểu SỐ DƯ (AR + MRR) — khác bản chất <c>RevenueAndLedgerF19Tests</c> (dòng chảy
/// theo kỳ). Trước vòng này KHÔNG endpoint nào tổng hợp công nợ postpaid toàn hệ thống (chỉ có
/// <c>me/invoices</c> tự tra 1 org) và subscription không hề được quy đổi thành doanh thu định kỳ.
/// </summary>
public class FinanceSnapshotServiceTests
{
    private static readonly DateTime T0 = new(2026, 7, 1, 0, 0, 0, DateTimeKind.Utc);

    // FinanceSnapshotService gọi DateTime.UtcNow TRỰC TIẾP (không nhận clock tiêm vào) — mọi mốc
    // Active/Expired của subscription PHẢI neo theo đồng hồ THẬT lúc test chạy, không phải T0 cố định
    // (T0 chỉ an toàn cho invoice vì AR không phụ thuộc "bây giờ", chỉ phụ thuộc Status).
    private static readonly DateTime Now = DateTime.UtcNow;

    // ── seed ─────────────────────────────────────────────────────────────────────────────────

    // DB9 — invoices/subscriptions mang FK composite (owner_type,owner_id)→credit_accounts (Restrict):
    // insert 1 trong 2 bảng đó mà chưa có ví đứng trước sẽ nổ 'FOREIGN KEY constraint failed'.
    private static async Task EnsureCreditAccountAsync(PaymentTestDb tdb, OwnerType ownerType, Guid ownerId)
    {
        // UNIQUE(owner_type, owner_id) — vài test cố ý seed 2 subscription CÙNG chủ ví (chồng lấn),
        // gọi hàm này 2 lần cho cùng owner thì lần 2 phải là no-op, không đụng UNIQUE.
        if (await tdb.Db.CreditAccounts.AnyAsync(a => a.OwnerType == ownerType && a.OwnerId == ownerId))
            return;
        tdb.Db.CreditAccounts.Add(new CreditAccount
        {
            Id = Guid.NewGuid(),
            OwnerType = ownerType,
            OwnerId = ownerId,
            PaymentMode = PaymentMode.Prepaid,
            Status = CreditAccountStatus.Active,
            RemainingCredits = 0,
            UpdatedAt = DateTime.UtcNow,
        });
        await tdb.Db.SaveChangesAsync();
    }

    private static async Task SeedInvoiceAsync(
        PaymentTestDb tdb, InvoiceStatus status, decimal amount, Guid? ownerId = null)
    {
        var owner = ownerId ?? Guid.NewGuid();
        await EnsureCreditAccountAsync(tdb, OwnerType.Org, owner);
        tdb.Db.Invoices.Add(new Invoice
        {
            Id = Guid.NewGuid(),
            OwnerType = OwnerType.Org,
            OwnerId = owner,
            PeriodStart = T0.AddMonths(-1),
            PeriodEnd = T0,
            InterviewCount = 10,
            UnitPrice = amount / 10,
            Amount = amount,
            Status = status,
            CreatedAt = T0,
            DueAt = T0.AddDays(15),
            PaidAt = status == InvoiceStatus.Paid ? T0.AddDays(1) : null,
        });
        await tdb.Db.SaveChangesAsync();
    }

    private static async Task<ProductPackage> SeedPackageAsync(PaymentTestDb tdb, long priceVnd)
    {
        var pkg = new ProductPackage
        {
            Id = Guid.NewGuid(),
            Name = "Gói test",
            Type = PackageType.Subscription,
            PriceVnd = priceVnd,
            IsActive = true,
            CreatedAt = T0,
            UpdatedAt = T0,
        };
        tdb.Db.ProductPackages.Add(pkg);
        await tdb.Db.SaveChangesAsync();
        return pkg;
    }

    // ownerType đã suy được từ Audience của Subscription (ck_sub_audience_owner) nên không truyền lặp;
    // hàm tự chọn Org↔B2B, User↔B2C.
    private static async Task SeedSubscriptionAsync(
        PaymentTestDb tdb, OwnerType ownerType, Guid ownerId, Guid? packageId,
        BillingCycle cycle, int tierRank = 1, SubscriptionStatus status = SubscriptionStatus.Active,
        SubscriptionSource source = SubscriptionSource.Purchase,
        DateTime? activatedAt = null, DateTime? expiresAt = null)
    {
        await EnsureCreditAccountAsync(tdb, ownerType, ownerId);
        tdb.Db.Subscriptions.Add(new Subscription
        {
            Id = Guid.NewGuid(),
            OwnerType = ownerType,
            OwnerId = ownerId,
            PackageId = packageId,
            Audience = ownerType == OwnerType.Org ? PlanAudience.B2B : PlanAudience.B2C,
            TierCode = $"tier-{tierRank}",
            TierRank = tierRank,
            // Credit (không Metered) — tránh ck_sub_metered_quota (đòi monthly_quota>0); MRR không phụ
            // thuộc InterviewFunding, chỉ phụ thuộc Package.PriceVnd + BillingCycle.
            InterviewFunding = InterviewFunding.Credit,
            Source = source,
            BillingCycle = cycle,
            Status = status,
            ActivatedAt = activatedAt ?? Now.AddDays(-1),
            StartedAt = activatedAt ?? Now.AddDays(-1),
            ExpiresAt = expiresAt ?? Now.AddDays(30),
            CreatedAt = Now.AddDays(-1),
            UpdatedAt = Now.AddDays(-1),
        });
        await tdb.Db.SaveChangesAsync();
    }

    // ── AR ───────────────────────────────────────────────────────────────────────────────────

    [Fact]
    public async Task AR_TachIssuedVaOverdue_BoQuaPaidVaVoid()
    {
        using var tdb = new PaymentTestDb();
        await SeedInvoiceAsync(tdb, InvoiceStatus.Issued, 1_000_000);
        await SeedInvoiceAsync(tdb, InvoiceStatus.Issued, 500_000);
        await SeedInvoiceAsync(tdb, InvoiceStatus.Overdue, 2_000_000);
        await SeedInvoiceAsync(tdb, InvoiceStatus.Paid, 900_000);
        await SeedInvoiceAsync(tdb, InvoiceStatus.Void, 300_000);

        var r = await new FinanceSnapshotService(tdb.Db).GetSnapshotAsync();

        Assert.Equal(1_500_000m, r.OutstandingReceivables.IssuedVnd);
        Assert.Equal(2, r.OutstandingReceivables.IssuedCount);
        Assert.Equal(2_000_000m, r.OutstandingReceivables.OverdueVnd);
        Assert.Equal(1, r.OutstandingReceivables.OverdueCount);
        // Paid (900k) và Void (300k) KHÔNG được cộng vào tổng — nếu lọt vào thì TotalVnd sẽ là 4.7M.
        Assert.Equal(3_500_000m, r.OutstandingReceivables.TotalVnd);
    }

    // ── MRR ──────────────────────────────────────────────────────────────────────────────────

    [Fact]
    public async Task MRR_QuyDoiAnnualVeThang()
    {
        using var tdb = new PaymentTestDb();
        var monthlyPkg = await SeedPackageAsync(tdb, 100_000);
        var annualPkg = await SeedPackageAsync(tdb, 1_200_000);

        await SeedSubscriptionAsync(tdb, OwnerType.User, Guid.NewGuid(), monthlyPkg.Id, BillingCycle.Monthly);
        await SeedSubscriptionAsync(tdb, OwnerType.Org, Guid.NewGuid(), annualPkg.Id, BillingCycle.Annual);

        var r = await new FinanceSnapshotService(tdb.Db).GetSnapshotAsync();

        // 100.000 (đã là giá tháng) + 1.200.000/12 = 100.000 → tổng 200.000, KHÔNG phải 100.000+1.200.000.
        Assert.Equal(200_000m, r.MrrVnd);
        Assert.Equal(2, r.ActiveSubscriptionCount);
    }

    [Fact]
    public async Task MRR_ChuViCoHaiSubscriptionChongLan_ChiTinhGoiTierCaoHon_KhongDoubleCount()
    {
        using var tdb = new PaymentTestDb();
        var cheapPkg = await SeedPackageAsync(tdb, 100_000);
        var expensivePkg = await SeedPackageAsync(tdb, 500_000);
        var owner = Guid.NewGuid();

        // Cùng 1 chủ ví, 2 row Active chồng lấn (nâng cấp giữa kỳ) — TierRank khác nhau.
        await SeedSubscriptionAsync(tdb, OwnerType.User, owner, cheapPkg.Id, BillingCycle.Monthly, tierRank: 1);
        await SeedSubscriptionAsync(tdb, OwnerType.User, owner, expensivePkg.Id, BillingCycle.Monthly, tierRank: 2);

        var r = await new FinanceSnapshotService(tdb.Db).GetSnapshotAsync();

        // CHỈ gói TierRank cao hơn (500k) được tính — không phải 100k+500k=600k.
        Assert.Equal(500_000m, r.MrrVnd);
        Assert.Equal(1, r.ActiveSubscriptionCount);
    }

    [Fact]
    public async Task MRR_AdminGrant_KhongTinhVaoDoanhThu()
    {
        using var tdb = new PaymentTestDb();
        var pkg = await SeedPackageAsync(tdb, 500_000);
        await SeedSubscriptionAsync(tdb, OwnerType.User, Guid.NewGuid(), pkg.Id, BillingCycle.Monthly,
            source: SubscriptionSource.AdminGrant);

        var r = await new FinanceSnapshotService(tdb.Db).GetSnapshotAsync();

        Assert.Equal(0m, r.MrrVnd);
        Assert.Equal(0, r.ActiveSubscriptionCount);
    }

    [Fact]
    public async Task MRR_HetHanHoacBiHuy_KhongTinh()
    {
        using var tdb = new PaymentTestDb();
        var pkg = await SeedPackageAsync(tdb, 500_000);

        // Status=Expired (đã đóng dấu) dù expires_at thật ra vẫn ở tương lai (data lỗi thời, sweeper
        // đóng dấu sớm) — vẫn KHÔNG tính, vì Status không phải Active.
        await SeedSubscriptionAsync(tdb, OwnerType.User, Guid.NewGuid(), pkg.Id, BillingCycle.Monthly,
            status: SubscriptionStatus.Expired, expiresAt: Now.AddDays(60));

        // Status=Active nhưng expires_at ĐÃ QUA (sweeper chưa kịp đóng dấu Expired) — cũng KHÔNG tính.
        await SeedSubscriptionAsync(tdb, OwnerType.User, Guid.NewGuid(), pkg.Id, BillingCycle.Monthly,
            status: SubscriptionStatus.Active, activatedAt: Now.AddDays(-40), expiresAt: Now.AddDays(-10));

        var r = await new FinanceSnapshotService(tdb.Db).GetSnapshotAsync();

        Assert.Equal(0m, r.MrrVnd);
        Assert.Equal(0, r.ActiveSubscriptionCount);
    }

    [Fact]
    public async Task MRR_PackageIdNull_KhongCrash_KhongTinhTien()
    {
        using var tdb = new PaymentTestDb();
        var pkg = await SeedPackageAsync(tdb, 300_000);

        await SeedSubscriptionAsync(tdb, OwnerType.User, Guid.NewGuid(), packageId: null, BillingCycle.Monthly);
        await SeedSubscriptionAsync(tdb, OwnerType.User, Guid.NewGuid(), pkg.Id, BillingCycle.Monthly);

        var r = await new FinanceSnapshotService(tdb.Db).GetSnapshotAsync();

        Assert.Equal(300_000m, r.MrrVnd);
        // Row PackageId=null vẫn là một subscription Active hợp lệ (chỉ không biết giá) → vẫn đếm.
        Assert.Equal(2, r.ActiveSubscriptionCount);
    }

    [Fact]
    public async Task Snapshot_DbRong_MoiFieldBangKhong_KhongException()
    {
        using var tdb = new PaymentTestDb();

        var r = await new FinanceSnapshotService(tdb.Db).GetSnapshotAsync();

        Assert.Equal(0m, r.OutstandingReceivables.TotalVnd);
        Assert.Equal(0, r.OutstandingReceivables.IssuedCount);
        Assert.Equal(0, r.OutstandingReceivables.OverdueCount);
        Assert.Equal(0m, r.MrrVnd);
        Assert.Equal(0, r.ActiveSubscriptionCount);
    }
}
