using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using PaymentService.Models;

namespace Isas.PaymentService.Services;

/// <summary>Canonical JSON contract returned to downstream entitlement consumers.</summary>
public sealed record EntitlementSnapshot(
    PlanAudience Audience, string Code, int Rank, InterviewFunding Funding, int? MonthlyQuota,
    bool AdaptiveEnabled, int? AdaptiveMaxQuestions, int? AdaptiveMaxFollowups,
    bool GroundingEnabled, int SelfConsistencyN, bool CvAnalysisIncluded, bool RepoAnalysisIncluded,
    bool RoadmapEnabled, int? MaxActiveCampaigns, int? MaxCandidatesCap, int? SeatCount,
    bool PostpaidEligible, string EntitlementsJson, int EntitlementsVersion)
{
    public string Json { get; private init; } = "";
    public string Hash { get; private init; } = "";

    public static EntitlementSnapshot Create(Plan plan)
    {
        var value = new EntitlementSnapshot(plan.Audience, plan.Code, plan.Rank, plan.InterviewFunding,
            plan.MonthlyQuota, plan.AdaptiveEnabled, plan.AdaptiveMaxQuestions, plan.AdaptiveMaxFollowups,
            plan.GroundingEnabled, plan.SelfConsistencyN, plan.CvAnalysisIncluded, plan.RepoAnalysisIncluded,
            plan.RoadmapEnabled, plan.MaxActiveCampaigns, plan.MaxCandidatesCap, plan.SeatCount,
            plan.PostpaidEligible, plan.EntitlementsJson, plan.EntitlementsVersion);
        var json = JsonSerializer.Serialize(value, JsonOptions);
        return value with { Json = json, Hash = Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(json))).ToLowerInvariant() };
    }

    private static readonly JsonSerializerOptions JsonOptions = new(JsonSerializerDefaults.Web);
}
