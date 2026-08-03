namespace Isas.InterviewService.Models;

/// <summary>Stable, local representation of Payment's resolved B2C entitlement.</summary>
public sealed record EntitlementSnapshot(
    string Source, string TierCode, int TierRank, bool AdaptiveEnabled, int MaxQuestions,
    int MaxFollowUps, bool GroundingEnabled, int SelfConsistencyN, bool CvAnalysisIncluded,
    bool RepoAnalysisIncluded, bool RoadmapEnabled)
{
    public static readonly EntitlementSnapshot Free = new(
        "free-default", "free", 0, false, 0, 0, false, 1, false, false, false);
}
