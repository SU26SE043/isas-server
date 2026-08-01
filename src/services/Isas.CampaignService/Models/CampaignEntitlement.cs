namespace Isas.CampaignService.Models;

/// <summary>Local, fail-closed projection of Payment's B2B entitlement snapshot.</summary>
public sealed record CampaignEntitlement(
    string Source, string TierCode, int TierRank, int MaxActiveCampaigns,
    int MaxCandidatesCap, bool AdaptiveEnabled, bool GroundingEnabled, bool PostpaidEligible)
{
    public static readonly CampaignEntitlement Starter = new(
        "starter-fallback", "starter", 0, 1, 25, false, false, false);
}
