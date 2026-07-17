using Isas.InterviewService.DTOs;

namespace Isas.InterviewService.Services.Interfaces;

public interface IPracticeService
{
    Task<PracticeSessionResponse> CreateSessionAsync(
        Guid candidateId, CreatePracticeSessionRequest request, CancellationToken ct = default);

    // BC14 — /start roadmap lesson: session B2C bình thường nhưng sessionId do caller cấp (để link lesson
    // sau khi tạo, thoả FK) + câu hỏi bám focusCriteria của milestone. Reserve/gen/BK12 như CreateSessionAsync.
    Task<PracticeSessionResponse> CreateLessonSessionAsync(
        Guid candidateId, CreatePracticeSessionRequest request, Guid sessionId,
        IReadOnlyList<string>? focusCriteria, CancellationToken ct = default);

    // I1: tạo session B2B (gắn campaign_id) + materialize tiêu chí campaign → rubric_criteria(campaign_id).
    Task<PracticeSessionResponse> CreateCampaignSessionAsync(
        Guid candidateId, CreateCampaignSessionRequest request, CancellationToken ct = default);

    // D2: create-or-get session B2B idempotent theo (candidateId, campaignId). Session chưa terminal →
    // trả session cũ (kèm câu hỏi/đáp án); chưa có → CreateCampaignSessionAsync (I1). Cho phép Campaign
    // /start bấm nhiều lần vẫn ra CÙNG session (không đẻ trùng).
    Task<PracticeSessionResponse> GetOrCreateCampaignSessionAsync(
        Guid candidateId, CreateCampaignSessionRequest request, CancellationToken ct = default);

    Task SubmitSessionAsync(
        Guid candidateId, Guid sessionId, CancellationToken ct = default);

    Task<PracticeSessionResponse?> GetSessionAsync(
        Guid candidateId, Guid sessionId, CancellationToken ct = default);

    Task<IReadOnlyList<PracticeSessionSummary>> GetHistoryAsync(
        Guid candidateId, CancellationToken ct = default);

    // DB18 — Payment gọi (internal) để phát hiện orphan reservation: trả TẬP CON sessionIds thực sự có
    // row practice_sessions (bất kể status). Reservation Reserved mà session KHÔNG tồn tại (crash giữa
    // reserve↔insert lúc Start) là orphan → Payment release. Chỉ đọc, không phân biệt owner/campaign.
    Task<IReadOnlyList<Guid>> GetExistingSessionIdsAsync(
        IReadOnlyList<Guid> sessionIds, CancellationToken ct = default);

    // AI4 — CampaignService (HR, internal) đọc transcript + nhận xét AI per-criterion + cờ needs_review
    // của 1 buổi để surface cho HR. CÙNG truy vấn như GetSessionAsync (questions→answers→Scores + MapAnswer)
    // NHƯNG KHÔNG check chủ session (máy-máy, X-Internal-Token; Campaign đã gate org+ranking phía nó).
    // Session không tồn tại → null (controller 404). Không phân biệt B2B/B2C (dùng cho bảng kết quả B2B).
    Task<IReadOnlyList<QuestionResponse>?> GetSessionAnswersInternalAsync(
        Guid sessionId, CancellationToken ct = default);
}