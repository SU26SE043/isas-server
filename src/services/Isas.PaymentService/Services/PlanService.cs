using Microsoft.Extensions.Options;
using PaymentService.Models;

namespace Isas.PaymentService.Services;

/// <summary>Catalog validation kept in Payment so no caller can manufacture an unsafe tier.</summary>
public sealed class PlanService
{
    private readonly TieringSettings _settings;

    public PlanService(IOptions<TieringSettings> settings) => _settings = settings.Value;

    public void Validate(Plan plan)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(plan.Code);
        ArgumentException.ThrowIfNullOrWhiteSpace(plan.Name);
        if (plan.Rank < 0) throw new ArgumentException("Plan rank must be >= 0.");
        if (plan.SelfConsistencyN < 1) throw new ArgumentException("SelfConsistencyN must be >= 1.");
        if (plan.MaxQuestionsCap is < 0 or > 20)
            throw new ArgumentException("MaxQuestionsCap must be between 0 and 20.");
        if (!plan.AdaptiveEnabled &&
            (plan.AdaptiveMaxQuestions is not null || plan.AdaptiveMaxFollowups is not null))
            throw new ArgumentException("Adaptive caps require AdaptiveEnabled.");
        if (plan.InterviewFunding == InterviewFunding.Metered && plan.MonthlyQuota is not > 0)
            throw new ArgumentException("Metered plans require a positive MonthlyQuota.");
        if (plan.InterviewFunding == InterviewFunding.Unlimited && !_settings.AllowUnlimitedPlans)
            throw new ArgumentException("Unlimited plans are disabled by Tiering:AllowUnlimitedPlans.");
        if (plan.Audience == PlanAudience.B2C &&
            (plan.MaxActiveCampaigns is not null || plan.MaxCandidatesCap is not null ||
             plan.PostpaidEligible || plan.SeatCount is not null))
            throw new ArgumentException("B2C plans cannot carry B2B entitlements.");
    }
}
