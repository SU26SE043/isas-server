namespace Isas.CampaignService.Services
{
    /// <summary>D2 — gọi InterviewService /internal/sessions/campaign (create-or-get session B2B, máy-máy).</summary>
    public interface ICampaignSessionClient
    {
        // BK18 — expiresAt = campaigns.expires_at; Interview set session.Deadline (I2) → sweeper auto-submit/
        // abandon quá hạn. null (B2C hoặc campaign không đặt hạn) = không hard-deadline.
        // BK14 — orgId = chủ ví credit (Campaign.OrgId) → Interview reserve owner=Org (PAY-6).
        // Ví org hết credit → InsufficientOrgCreditException (402), KHÔNG tạo session.
        Task<CampaignSessionResult> CreateOrGetSessionAsync(
            Guid candidateId, Guid campaignId, Guid orgId, string jobCategory,
            IReadOnlyList<string> questions, IReadOnlyList<SessionCriterionInput> criteria,
            DateTime? expiresAt = null,
            CancellationToken ct = default);
    }

    public record SessionCriterionInput(string Name, string? Description, decimal Weight, int MaxScore);

    public record CampaignSessionResult(Guid SessionId, IReadOnlyList<SessionQuestion> Questions);

    public record SessionQuestion(Guid Id, int OrderNo, string Content, int TimeLimitSec);
}
