using Isas.PaymentService.Services;
using Microsoft.Extensions.Options;
using PaymentService.Models;

namespace Isas.PaymentService.Tests;

/// <summary>
/// Bảng giá cho người mua: catalog public + gói đang dùng của caller.
///
/// Hai bất biến đắt nhất được khoá ở đây:
///   • catalog KHÔNG được rò gói đã ngừng bán (bán thứ không còn trong danh mục = nhận tiền cho hư không);
///   • hạn mức còn lại phải khớp ĐÚNG guard của <c>ReserveAsync</c> (<c>used + reserved + 1 &lt;= quota</c>),
///     kể cả phần đang giữ — lệch chỗ này thì người dùng bị 402 trong lúc màn hình bảo còn lượt, và không
///     có triệu chứng nào khác để lần ra.
/// </summary>
public class PlanCatalogServiceTests
{
    /// <summary>
    /// Gọi <c>CreditAccountService.MeteredPeriodStart</c> (internal) qua reflection — cùng lối
    /// <see cref="MeteredCreditServiceTests"/>. Cố ý KHÔNG chép lại công thức mốc kỳ vào test: chép ra là
    /// test sẽ tự xác nhận bản sao của chính nó, đúng lúc thứ cần kiểm là "hai bên có cùng mốc kỳ không".
    /// </summary>
    private static DateTime PeriodStart(DateTime now, short? anchorDay) => (DateTime)typeof(CreditAccountService)
        .GetMethod("MeteredPeriodStart", System.Reflection.BindingFlags.NonPublic | System.Reflection.BindingFlags.Static)!
        .Invoke(null, [now, anchorDay])!;

    private static PlanCatalogService Svc(PaymentTestDb t, bool tieringEnabled = true)
    {
        var db = t.NewContext();
        return new PlanCatalogService(db, new EntitlementResolver(db),
            Options.Create(new TieringSettings { Enabled = tieringEnabled }));
    }

    [Fact]
    public async Task Catalog_B2C_TraDungBaGoiTheoRank()
    {
        using var t = new PaymentTestDb();

        var plans = await Svc(t).GetCatalogAsync(PlanAudience.B2C);

        Assert.Equal(["free", "plus", "pro"], plans.Select(p => p.Code));
        Assert.All(plans, p => Assert.Equal(PlanAudience.B2C, p.Audience));
    }

    [Fact]
    public async Task Catalog_B2B_TraDungBaGoiTheoRank()
    {
        using var t = new PaymentTestDb();

        var plans = await Svc(t).GetCatalogAsync(PlanAudience.B2B);

        Assert.Equal(["starter", "business", "enterprise"], plans.Select(p => p.Code));
    }

    [Fact]
    public async Task Catalog_KhongLocAudience_TraCaHaiDong()
    {
        using var t = new PaymentTestDb();

        var plans = await Svc(t).GetCatalogAsync(null);

        Assert.Contains(plans, p => p.Audience == PlanAudience.B2C);
        Assert.Contains(plans, p => p.Audience == PlanAudience.B2B);
    }

    [Fact]
    public async Task Catalog_GoiNgungBan_KhongLoRaNguoiMua()
    {
        using var t = new PaymentTestDb();
        var pro = t.Db.Plans.Single(p => p.Audience == PlanAudience.B2C && p.Code == "pro");
        pro.IsActive = false;
        await t.Db.SaveChangesAsync();

        var plans = await Svc(t).GetCatalogAsync(PlanAudience.B2C);

        Assert.DoesNotContain(plans, p => p.Code == "pro");
    }

    [Fact]
    public async Task Catalog_KemPackageIdVaGia_DeFEMuaDuoc()
    {
        using var t = new PaymentTestDb();
        var pro = t.Db.Plans.Single(p => p.Audience == PlanAudience.B2C && p.Code == "pro");
        var pkg = new ProductPackage
        {
            Id = Guid.NewGuid(), Name = "Pro tháng", Type = PackageType.Subscription,
            PriceVnd = 199_000, DurationDays = 30, PlanId = pro.Id,
            Audience = PlanAudience.B2C, IsActive = true, CreatedAt = DateTime.UtcNow
        };
        t.Db.ProductPackages.Add(pkg);
        await t.Db.SaveChangesAsync();

        var plans = await Svc(t).GetCatalogAsync(PlanAudience.B2C);

        var option = Assert.Single(plans.Single(p => p.Code == "pro").Packages);
        Assert.Equal(pkg.Id, option.PackageId);
        Assert.Equal(199_000, option.PriceVnd);
        Assert.Equal(30, option.DurationDays);
        // Gói free không bán SKU nào — FE dựa vào mảng rỗng để ẩn nút Mua.
        Assert.Empty(plans.Single(p => p.Code == "free").Packages);
    }

    [Fact]
    public async Task Catalog_PackageNgungBan_KhongTraLamLuaChonMua()
    {
        using var t = new PaymentTestDb();
        var pro = t.Db.Plans.Single(p => p.Audience == PlanAudience.B2C && p.Code == "pro");
        t.Db.ProductPackages.Add(new ProductPackage
        {
            Id = Guid.NewGuid(), Name = "Pro cũ", Type = PackageType.Subscription,
            PriceVnd = 99_000, DurationDays = 30, PlanId = pro.Id,
            Audience = PlanAudience.B2C, IsActive = false, CreatedAt = DateTime.UtcNow
        });
        await t.Db.SaveChangesAsync();

        var plans = await Svc(t).GetCatalogAsync(PlanAudience.B2C);

        // Gói VẪN hiện (để so quyền lợi), chỉ mất lựa chọn mua.
        Assert.Contains(plans, p => p.Code == "pro");
        Assert.Empty(plans.Single(p => p.Code == "pro").Packages);
    }

    [Fact]
    public async Task Catalog_PackageCreditPack_KhongBiGanNhamVaoGoi()
    {
        using var t = new PaymentTestDb();
        var pro = t.Db.Plans.Single(p => p.Audience == PlanAudience.B2C && p.Code == "pro");
        // Pack credit (OneTime) trỏ nhầm plan_id: nếu lọt vào Packages thì FE bán "gói Pro" mà thực ra
        // người mua chỉ nhận credit lẻ, không có quyền lợi tier nào.
        t.Db.ProductPackages.Add(new ProductPackage
        {
            Id = Guid.NewGuid(), Name = "10 credit", Type = PackageType.OneTime,
            PriceVnd = 50_000, InterviewCredits = 10, PlanId = pro.Id,
            IsActive = true, CreatedAt = DateTime.UtcNow
        });
        await t.Db.SaveChangesAsync();

        var plans = await Svc(t).GetCatalogAsync(PlanAudience.B2C);

        Assert.Empty(plans.Single(p => p.Code == "pro").Packages);
    }

    [Fact]
    public async Task MyPlan_ChuaMuaGi_TraGoiFree_KhongPhaiLoi()
    {
        using var t = new PaymentTestDb();

        var me = await Svc(t).GetMyPlanAsync(OwnerType.User, Guid.NewGuid());

        Assert.Equal("free", me.TierCode);
        Assert.Equal("Free", me.TierName);
        Assert.False(me.IsPaid);
        Assert.Null(me.QuotaRemaining);
    }

    [Fact]
    public async Task MyPlan_Org_ChuaMuaGi_TraStarter()
    {
        using var t = new PaymentTestDb();

        var me = await Svc(t).GetMyPlanAsync(OwnerType.Org, Guid.NewGuid());

        Assert.Equal(PlanAudience.B2B, me.Audience);
        Assert.Equal("starter", me.TierCode);
    }

    [Fact]
    public async Task MyPlan_GoiMetered_TruCaLuotDANGGIU_KhopGuardReserve()
    {
        using var t = new PaymentTestDb();
        var owner = Guid.NewGuid();
        var sub = SeedMetered(t, owner, quota: 30, anchorDay: 1);
        var period = PeriodStart(DateTime.UtcNow, 1);
        t.Db.SubscriptionMeters.Add(new SubscriptionMeter
        {
            SubscriptionId = sub.Id, PeriodStart = period, UsedCount = 8, ReservedCount = 2
        });
        await t.Db.SaveChangesAsync();

        var me = await Svc(t).GetMyPlanAsync(OwnerType.User, owner);

        Assert.Equal(30, me.MonthlyQuota);
        Assert.Equal(8, me.QuotaUsed);
        Assert.Equal(2, me.QuotaReserved);
        // 30 − 8 − 2: chỗ đang giữ CŨNG đã tiêu hạn mức (ReserveAsync gác `used + reserved + 1 <= quota`).
        Assert.Equal(20, me.QuotaRemaining);
        Assert.Equal(period, me.PeriodStart);
        Assert.True(me.IsPaid);
        Assert.NotNull(me.ExpiresAt);
    }

    [Fact]
    public async Task MyPlan_GoiMetered_ChuaCoMeter_TraNguyenQuota()
    {
        using var t = new PaymentTestDb();
        var owner = Guid.NewGuid();
        SeedMetered(t, owner, quota: 10, anchorDay: 1);
        await t.Db.SaveChangesAsync();

        var me = await Svc(t).GetMyPlanAsync(OwnerType.User, owner);

        Assert.Equal(0, me.QuotaUsed);
        Assert.Equal(10, me.QuotaRemaining);
    }

    [Fact]
    public async Task MyPlan_QuotaCanKiet_TraVe0_KhongAm()
    {
        using var t = new PaymentTestDb();
        var owner = Guid.NewGuid();
        var sub = SeedMetered(t, owner, quota: 5, anchorDay: 1);
        t.Db.SubscriptionMeters.Add(new SubscriptionMeter
        {
            SubscriptionId = sub.Id,
            PeriodStart = PeriodStart(DateTime.UtcNow, 1),
            UsedCount = 5, ReservedCount = 3
        });
        await t.Db.SaveChangesAsync();

        var me = await Svc(t).GetMyPlanAsync(OwnerType.User, owner);

        Assert.Equal(0, me.QuotaRemaining);
    }

    [Fact]
    public async Task MyPlan_MeterKyKHAC_KhongBiTinhVaoKyHienTai()
    {
        using var t = new PaymentTestDb();
        var owner = Guid.NewGuid();
        var sub = SeedMetered(t, owner, quota: 30, anchorDay: 1);
        var previous = PeriodStart(DateTime.UtcNow, 1).AddMonths(-1);
        t.Db.SubscriptionMeters.Add(new SubscriptionMeter
        {
            SubscriptionId = sub.Id, PeriodStart = previous, UsedCount = 29, ReservedCount = 1
        });
        await t.Db.SaveChangesAsync();

        var me = await Svc(t).GetMyPlanAsync(OwnerType.User, owner);

        // Kỳ trước tiêu hết KHÔNG được kéo sang kỳ này (S11: kỳ mới nhận quota tươi).
        Assert.Equal(30, me.QuotaRemaining);
    }

    [Fact]
    public async Task MyPlan_TieringTat_BaoCoDeFEKhongBanThuChuaChayDuoc()
    {
        using var t = new PaymentTestDb();

        var on = await Svc(t, tieringEnabled: true).GetMyPlanAsync(OwnerType.User, Guid.NewGuid());
        var off = await Svc(t, tieringEnabled: false).GetMyPlanAsync(OwnerType.User, Guid.NewGuid());

        Assert.True(on.TieringEnabled);
        Assert.False(off.TieringEnabled);
    }

    [Fact]
    public async Task MyPlan_CatalogMatTenGoi_LuiVeMaGoi_KhongNem()
    {
        using var t = new PaymentTestDb();
        t.Db.Plans.RemoveRange(t.Db.Plans);
        await t.Db.SaveChangesAsync();

        var me = await Svc(t).GetMyPlanAsync(OwnerType.User, Guid.NewGuid());

        Assert.Equal("free", me.TierCode);
        Assert.Equal("free", me.TierName);
    }

    private static Subscription SeedMetered(PaymentTestDb t, Guid ownerId, int quota, short anchorDay)
    {
        var plan = t.Db.Plans.Single(p => p.Audience == PlanAudience.B2C && p.Code == "plus");
        var now = DateTime.UtcNow;
        // DB9 — subscriptions có composite FK (owner_type, owner_id) → credit_accounts: người mua gói
        // luôn có ví. Thiếu ví thì insert đổ FK chứ không phải lỗi của phần đang test.
        t.Db.CreditAccounts.Add(new CreditAccount
        {
            Id = Guid.NewGuid(), OwnerType = OwnerType.User, OwnerId = ownerId,
            PaymentMode = PaymentMode.Prepaid, Status = CreditAccountStatus.Active, UpdatedAt = now
        });
        var sub = new Subscription
        {
            Id = Guid.NewGuid(), OwnerType = OwnerType.User, OwnerId = ownerId,
            PlanId = plan.Id, Audience = PlanAudience.B2C, TierCode = plan.Code, TierRank = plan.Rank,
            InterviewFunding = InterviewFunding.Metered, MonthlyQuota = quota, MeterAnchorDay = anchorDay,
            BillingCycle = BillingCycle.Monthly, Status = SubscriptionStatus.Active,
            StartedAt = now.AddDays(-3), ExpiresAt = now.AddDays(27),
            ActivatedAt = now.AddDays(-3), CreatedAt = now.AddDays(-3)
        };
        t.Db.Subscriptions.Add(sub);
        return sub;
    }
}
