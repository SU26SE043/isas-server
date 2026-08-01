using PaymentService.Models;

namespace Isas.PaymentService.DTOs;

/// <summary>Public admin-catalog projection; never exposes the persistence entity directly.</summary>
public sealed record PlanResponse(
    Guid Id, PlanAudience Audience, string Code, string Name, int Rank, InterviewFunding InterviewFunding,
    int? MonthlyQuota, bool AdaptiveEnabled, int? AdaptiveMaxQuestions, int? AdaptiveMaxFollowups,
    bool GroundingEnabled, int SelfConsistencyN, bool CvAnalysisIncluded, bool RepoAnalysisIncluded,
    bool RoadmapEnabled, int? MaxQuestionsCap, int? MaxActiveCampaigns, int? MaxCandidatesCap,
    bool PostpaidEligible, int? SeatCount, int EntitlementsVersion, bool IsActive)
{
    public static PlanResponse From(Plan plan) => new(plan.Id, plan.Audience, plan.Code, plan.Name, plan.Rank,
        plan.InterviewFunding, plan.MonthlyQuota, plan.AdaptiveEnabled, plan.AdaptiveMaxQuestions,
        plan.AdaptiveMaxFollowups, plan.GroundingEnabled, plan.SelfConsistencyN, plan.CvAnalysisIncluded,
        plan.RepoAnalysisIncluded, plan.RoadmapEnabled, plan.MaxQuestionsCap, plan.MaxActiveCampaigns,
        plan.MaxCandidatesCap, plan.PostpaidEligible, plan.SeatCount, plan.EntitlementsVersion, plan.IsActive);
}
