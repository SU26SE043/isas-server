namespace Isas.CampaignService.Services
{
    /// <summary>D2 — gọi InterviewService /internal/sessions/campaign (create-or-get session B2B, máy-máy).</summary>
    public interface ICampaignSessionClient
    {
        Task<CampaignSessionResult> CreateOrGetSessionAsync(
            Guid candidateId, Guid campaignId, string jobCategory,
            IReadOnlyList<string> questions, IReadOnlyList<SessionCriterionInput> criteria,
            CancellationToken ct = default);
    }

    public record SessionCriterionInput(string Name, string? Description, decimal Weight, int MaxScore);

    public record CampaignSessionResult(Guid SessionId, IReadOnlyList<SessionQuestion> Questions);

    public record SessionQuestion(Guid Id, int OrderNo, string Content, int TimeLimitSec);
}
