namespace Isas.CampaignService.Models;

/// <summary>Local, fail-closed projection of Payment's B2B entitlement snapshot.</summary>
public sealed record CampaignEntitlement(
    string Source, string TierCode, int TierRank, int MaxActiveCampaigns,
    int MaxCandidatesCap, bool AdaptiveEnabled, bool GroundingEnabled, bool PostpaidEligible)
{
    public static readonly CampaignEntitlement Starter = new(
        "starter-fallback", "starter", 0, 1, 25, false, false, false);

    // Tiering rollout is additive: before it is enabled, preserve the pre-tier B2B behaviour
    // rather than silently constraining existing organisations to Starter.
    public static readonly CampaignEntitlement Legacy = new(
        "tiering-disabled", "legacy", 0, int.MaxValue, int.MaxValue, true, true, true);
}
