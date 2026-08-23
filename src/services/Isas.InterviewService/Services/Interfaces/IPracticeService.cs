using Isas.InterviewService.DTOs;
using Isas.Shared.Pagination;

namespace Isas.InterviewService.Services.Interfaces;

public interface IPracticeService
{
    /// <summary>
    /// SC3 — server-side preview so clients do not duplicate adaptive seed-count rules.
    /// <paramref name="language"/> must match the language the client will use when creating the
    /// session: the content-criteria count of that rubric is the FLOOR for the seed count, so a
    /// mismatched language previews a different number than the session will actually get.
    /// </summary>
    Task<PracticeSessionOptionsResponse> GetSessionOptionsAsync(
        Guid candidateId, string jobCategory, string? language = null, CancellationToken ct = default);

    Task<PracticeSessionResponse> CreateSessionAsync(
        Guid candidateId, CreatePracticeSessionRequest request, CancellationToken ct = default);

    // BC14 — /start roadmap lesson: session B2C bình thường nhưng sessionId do caller cấp (để link lesson
    // sau khi tạo, thoả FK) + câu hỏi bám focusCriteria của milestone. Reserve/gen/BK12 như CreateSessionAsync.
    //
    // `lessonContext` (chủ đề của ĐÚNG bài đang mở) là thứ phân biệt bài này với các bài KHÁC cùng
    // chặng: `focusCriteria` thuộc về CHẶNG nên mọi bài trong chặng gửi xuống y hệt nhau (đo trên
    // dev: 1 chặng / 4 bài / cùng 3 tiêu chí; trung bình 2,8 bài/chặng trên 87 chặng).
    // Optional để caller cũ (test gọi trực tiếp) không phải sửa; production LUÔN truyền.
    Task<PracticeSessionResponse> CreateLessonSessionAsync(
        Guid candidateId, CreatePracticeSessionRequest request, Guid sessionId,
        IReadOnlyList<string>? focusCriteria, LessonContext? lessonContext = null,
        CancellationToken ct = default);

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

    // Audio câu trả lời của chính candidate. Null = session/answer không tồn tại hoặc answer chưa có audio;
    // owner khác → UnauthorizedAccessException. Không trả SeaweedFS object key ra API.
    Task<AnswerAudioContent?> GetAnswerAudioAsync(
        Guid candidateId, Guid sessionId, Guid answerId, CancellationToken ct = default);

    // DB31 — keyset-paged (mẫu DB8): cursor opaque + limit opt-in; body giữ mảng JSON,
    // next-cursor trả ở header X-Next-Cursor. cursor=null ⇒ trang đầu.
    // status/excludeCampaign đều OPT-IN — vắng cả hai ⇒ shape và tập kết quả y hệt hành vi cũ
    // (trang "Lịch sử phỏng vấn" dùng chính endpoint này). status khớp fail-open như
    // ListAllCampaignsAsync (CampaignService): giá trị lạ KHÔNG parse được ⇒ filter đơn giản
    // không được áp (trả nguyên, không lọc gì), không 400 — đây là filter duyệt-danh-sách,
    // không phải input dẫn nghiệp vụ như RoadmapService.ValidateMode.
    Task<KeysetPage<PracticeSessionSummary>> GetHistoryAsync(
        Guid candidateId, string? cursor = null, int? limit = null,
        string? status = null, bool? excludeCampaign = null, CancellationToken ct = default);

    // DB18 — Payment gọi (internal) để phát hiện orphan reservation: trả TẬP CON sessionIds thực sự có
    // row practice_sessions (bất kể status). Reservation Reserved mà session KHÔNG tồn tại (crash giữa
    // reserve↔insert lúc Start) là orphan → Payment release. Chỉ đọc, không phân biệt owner/campaign.
    Task<IReadOnlyList<Guid>> GetExistingSessionIdsAsync(
        IReadOnlyList<Guid> sessionIds, CancellationToken ct = default);

    // R1 — như trên nhưng kèm TRẠNG THÁI (string, GEN-2). Payment cần phân biệt session đã terminal
    // (Scored → consume; SessionAbandoned/Failed → release) với session đang bay (SKIP): trước R1 chỗ
    // giữ của session terminal mà lỡ mất event settle thì KHÔNG AI DỌN → rò credit vĩnh viễn.
    Task<IReadOnlyList<SessionStateDto>> GetExistingSessionStatesAsync(
        IReadOnlyList<Guid> sessionIds, CancellationToken ct = default);

    // AI4 — CampaignService (HR, internal) đọc transcript + nhận xét AI per-criterion + cờ needs_review
    // của 1 buổi để surface cho HR. CÙNG truy vấn như GetSessionAsync (questions→answers→Scores + MapAnswer)
    // NHƯNG KHÔNG check chủ session (máy-máy, X-Internal-Token; Campaign đã gate org+ranking phía nó).
    // Session không tồn tại → null (controller 404). Không phân biệt B2B/B2C (dùng cho bảng kết quả B2B).
    Task<IReadOnlyList<QuestionResponse>?> GetSessionAnswersInternalAsync(
        Guid sessionId, CancellationToken ct = default);
}
