using Isas.PaymentService.Services;
using Microsoft.Extensions.Options;
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

    private static Plan NewPlan(string code = "plus", int rank = 1) => new()
    {
        Id = Guid.NewGuid(), Audience = PlanAudience.B2C, Code = code, Name = code,
        Rank = rank, InterviewFunding = InterviewFunding.Metered, MonthlyQuota = 30
    };
}
