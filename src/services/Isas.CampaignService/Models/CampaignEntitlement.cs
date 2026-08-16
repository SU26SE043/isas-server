namespace Isas.CampaignService.Models;

/// <summary>Local, fail-closed projection of Payment's B2B entitlement snapshot.</summary>
public sealed record CampaignEntitlement(
    string Source, string TierCode, int TierRank, int MaxActiveCampaigns,
    int MaxCandidatesCap, bool AdaptiveEnabled, bool GroundingEnabled, bool PostpaidEligible)
{
    // Fail-closed cho những quyền lợi CÓ chi phí biên khác nhau thật (số campaign, số ứng viên,
    // grounding, postpaid). `AdaptiveEnabled` để TRUE vì adaptive là engine phỏng vấn chứ không phải
    // quyền lợi theo gói — mọi tier đều tiêu credit org cho mỗi buổi — nên một lần Payment sập không
    // được biến thành lời từ chối SAI ("gói starter không hỗ trợ adaptive") với HR đang dùng gói có nó.
    public static readonly CampaignEntitlement Starter = new(
        "starter-fallback", "starter", 0, 1, 25, true, false, false);

    // Tiering rollout is additive: before it is enabled, preserve the pre-tier B2B behaviour
    // rather than silently constraining existing organisations to Starter.
    public static readonly CampaignEntitlement Legacy = new(
        "tiering-disabled", "legacy", 0, int.MaxValue, int.MaxValue, true, true, true);
}
