using Isas.CampaignService.DTOs;

namespace Isas.CampaignService.Services
{
    /// <summary>D2 — gọi InterviewService /internal/sessions/campaign (create-or-get session B2B, máy-máy).</summary>
    public interface ICampaignSessionClient
    {
        // BK18 — expiresAt = campaigns.expires_at; Interview set session.Deadline (I2) → sweeper auto-submit/
        // abandon quá hạn. null (B2C hoặc campaign không đặt hạn) = không hard-deadline.
        // BK14 — orgId = chủ ví credit (Campaign.OrgId) → Interview reserve owner=Org (PAY-6).
        // Ví org hết credit → InsufficientOrgCreditException (402), KHÔNG tạo session.
        // INT-17 — adaptiveEnabled/maxFollowUps/maxQuestions = toggle + trần HR đặt trên campaign.
        // Interview đóng dấu lên session lúc tạo (null → tắt / mặc định).
        // INT-17b — maxDeepPerQuestion: null/0 giữ hành vi cũ (seed = toàn bộ campaign questions, câu
        // thích ứng chỉ thêm ở ĐUÔI sau khi trả lời hết seed); > 0 thì MỖI câu campaign mọc chuỗi đào
        // sâu XEN KẼ ngay sau nó (vẫn công bằng: cùng bộ câu gốc, cùng trần độ sâu cho mọi ứng viên).
        Task<CampaignSessionResult> CreateOrGetSessionAsync(
            Guid candidateId, Guid campaignId, Guid orgId, string jobCategory,
            IReadOnlyList<string> questions, IReadOnlyList<SessionCriterionInput> criteria,
            DateTime? expiresAt = null,
            bool? adaptiveEnabled = null, int? maxFollowUps = null, int? maxQuestions = null,
            int? maxDeepPerQuestion = null,
            CancellationToken ct = default,
            string seniority = "Junior");
        Task<CampaignSessionResult> CreateOrGetSessionAsync(Guid candidateId, Guid campaignId, Guid orgId, string jobCategory, IReadOnlyList<string> questions, IReadOnlyList<SessionCriterionInput> criteria, DateTime? expiresAt, bool? adaptiveEnabled, int? maxFollowUps, int? maxQuestions, int? maxDeepPerQuestion, string language, CancellationToken ct, string seniority = "Junior");

        // AI4 — HR đọc transcript + nhận xét AI per-criterion + cờ needs_review của 1 buổi (đối chiếu điểm
        // ranking). Gọi Interview GET /internal/sessions/{sessionId}/answers (máy-máy, X-Internal-Token).
        // Lỗi hạ tầng / non-success → DownstreamServiceException (502).
        Task<SessionTranscriptResponse> GetSessionTranscriptAsync(
            Guid sessionId, CancellationToken ct = default);
    }

    public record SessionCriterionInput(string Name, string? Description, decimal Weight, int MaxScore);

    public record CampaignSessionResult(Guid SessionId, IReadOnlyList<SessionQuestion> Questions);

    public record SessionQuestion(Guid Id, int OrderNo, string Content, int TimeLimitSec);
}
