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

    [Fact]
    public async Task Update_DefaultPlanIdentity_IsRejected()
    {
        using var t = new PaymentTestDb();
        var free = await t.Db.Plans.SingleAsync(p => p.Audience == PlanAudience.B2C && p.Code == "free");

        await Assert.ThrowsAsync<ArgumentException>(() => new PlanService(t.NewContext(), Options.Create(new TieringSettings()))
            .UpdateAsync(free.Id, Request(free, code: "renamed-free"), default));
    }

    [Fact]
    public async Task Deactivate_DefaultPlan_IsRejected()
    {
        using var t = new PaymentTestDb();
        var starter = await t.Db.Plans.SingleAsync(p => p.Audience == PlanAudience.B2B && p.Code == "starter");

        await Assert.ThrowsAsync<ArgumentException>(() => new PlanService(t.NewContext(), Options.Create(new TieringSettings()))
            .DeactivateAsync(starter.Id, default));
    }

    [Fact]
    public async Task Update_ReferencedPlanIdentity_IsRejected()
    {
        using var t = new PaymentTestDb(); var plan = NewPlan("custom");
        var owner = Guid.NewGuid();
        t.Db.AddRange(plan, new CreditAccount
        {
            Id = Guid.NewGuid(), OwnerType = OwnerType.User, OwnerId = owner,
            PaymentMode = PaymentMode.Prepaid, Status = CreditAccountStatus.Active, UpdatedAt = DateTime.UtcNow
        }, new Subscription
        {
            Id = Guid.NewGuid(), OwnerType = OwnerType.User, OwnerId = owner, PlanId = plan.Id,
            Audience = PlanAudience.B2C, TierCode = plan.Code, TierRank = plan.Rank,
            InterviewFunding = plan.InterviewFunding, MonthlyQuota = plan.MonthlyQuota,
            EntitlementSnapshot = "{}", EntitlementHash = "x", StartedAt = DateTime.UtcNow,
            ActivatedAt = DateTime.UtcNow, ExpiresAt = DateTime.UtcNow.AddDays(30), CreatedAt = DateTime.UtcNow
        });
        await t.Db.SaveChangesAsync();

        await Assert.ThrowsAsync<ArgumentException>(() => new PlanService(t.NewContext(), Options.Create(new TieringSettings()))
            .UpdateAsync(plan.Id, Request(plan, code: "renamed"), default));
    }

    [Fact]
    public async Task Update_DuplicateAudienceCode_ReturnsValidationError()
    {
        using var t = new PaymentTestDb(); var first = NewPlan("first"); var second = NewPlan("second");
        t.Db.AddRange(first, second); await t.Db.SaveChangesAsync();

        await Assert.ThrowsAsync<ArgumentException>(() => new PlanService(t.NewContext(), Options.Create(new TieringSettings()))
            .UpdateAsync(second.Id, Request(second, code: first.Code), default));
    }

    private static Plan NewPlan(string code = "plus", int rank = 1) => new()
    {
        Id = Guid.NewGuid(), Audience = PlanAudience.B2C, Code = code, Name = code,
        Rank = rank, InterviewFunding = InterviewFunding.Metered, MonthlyQuota = 30,
        CreatedAt = DateTime.UtcNow, UpdatedAt = DateTime.UtcNow
    };

    private static PlanRequest Request(Plan plan, string? code = null) => new()
    {
        Audience = plan.Audience, Code = code ?? plan.Code, Name = plan.Name, Rank = plan.Rank,
        InterviewFunding = plan.InterviewFunding, MonthlyQuota = plan.MonthlyQuota,
        AdaptiveEnabled = plan.AdaptiveEnabled, AdaptiveMaxQuestions = plan.AdaptiveMaxQuestions,
        AdaptiveMaxFollowups = plan.AdaptiveMaxFollowups, GroundingEnabled = plan.GroundingEnabled,
        SelfConsistencyN = plan.SelfConsistencyN, CvAnalysisIncluded = plan.CvAnalysisIncluded,
        RepoAnalysisIncluded = plan.RepoAnalysisIncluded, RoadmapEnabled = plan.RoadmapEnabled,
        MaxQuestionsCap = plan.MaxQuestionsCap, MaxActiveCampaigns = plan.MaxActiveCampaigns,
        MaxCandidatesCap = plan.MaxCandidatesCap, PostpaidEligible = plan.PostpaidEligible,
        SeatCount = plan.SeatCount, EntitlementsJson = plan.EntitlementsJson,
        EntitlementsVersion = plan.EntitlementsVersion, IsActive = plan.IsActive
    };
}
