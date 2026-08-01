using Isas.PaymentService.Services;
using Microsoft.Extensions.Options;
using Isas.PaymentService.DTOs;
using Microsoft.EntityFrameworkCore;
using PaymentService.Models;

namespace Isas.PaymentService.Tests;

public class PlanServiceTests
{
    [Fact]
    public void UnlimitedPlan_IsRejected_WhenFeatureFlagIsOff()
    {
        var service = new PlanService(Options.Create(new TieringSettings()));
        var plan = NewPlan();
        plan.InterviewFunding = InterviewFunding.Unlimited;
        Assert.Throws<ArgumentException>(() => service.Validate(plan));
    }

    [Fact]
    public void B2CPlan_CannotCarryB2BCaps()
    {
        var service = new PlanService(Options.Create(new TieringSettings()));
        var plan = NewPlan();
        plan.MaxActiveCampaigns = 1;
        Assert.Throws<ArgumentException>(() => service.Validate(plan));
    }

    [Fact]
    public void TierPriority_OrdersHighestRankFirst()
    {
        var ordered = new[] { NewPlan("plus", 1), NewPlan("pro", 2) }
            .AsQueryable().OrderByTierPriority().ToList();
        Assert.Equal("pro", ordered[0].Code);
    }

    [Fact]
    public void MeteredPlan_RequiresPositiveQuota()
    {
        var plan = NewPlan(); plan.MonthlyQuota = 0;
        Assert.Throws<ArgumentException>(() => new PlanService(Options.Create(new TieringSettings())).Validate(plan));
    }

    [Fact]
    public async Task Deactivate_PreservesPlanReferencedByPackage_AndPreventsNewSale()
    {
        using var t = new PaymentTestDb(); var plan = NewPlan("custom-plan");
        var package = new ProductPackage { Id = Guid.NewGuid(), Name = "sub", Type = PackageType.Subscription,
            PlanId = plan.Id, Audience = PlanAudience.B2C, PriceVnd = 1, DurationDays = 30, IsActive = true, CreatedAt = DateTime.UtcNow };
        t.Db.AddRange(plan, package); await t.Db.SaveChangesAsync();
        var service = new PlanService(t.NewContext(), Options.Create(new TieringSettings()));

        Assert.True(await service.DeactivateAsync(plan.Id, default));
        Assert.NotNull(await t.NewContext().Plans.FindAsync(plan.Id));
        Assert.False((await t.NewContext().Plans.FindAsync(plan.Id))!.IsActive);
        Assert.Null(await t.NewContext().Plans.AsNoTracking().SingleOrDefaultAsync(p => p.Id == package.PlanId && p.IsActive));
    }

    private static Plan NewPlan(string code = "plus", int rank = 1) => new()
    {
        Id = Guid.NewGuid(), Audience = PlanAudience.B2C, Code = code, Name = code,
        Rank = rank, InterviewFunding = InterviewFunding.Metered, MonthlyQuota = 30,
        CreatedAt = DateTime.UtcNow, UpdatedAt = DateTime.UtcNow
    };
}
