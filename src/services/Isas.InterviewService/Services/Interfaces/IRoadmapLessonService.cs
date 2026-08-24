using Isas.InterviewService.DTOs;

namespace Isas.InterviewService.Services.Interfaces;

// BC14 (D20) — thao tác cấp lesson trong roadmap ôn tập B2C: mở lesson (lý thuyết lazy) + /start luyện.
// Owner-only (khác chủ → 403; không có → 404). Session Scored→Done / Abandoned→Theory móc ở luồng đóng
// session (SessionScoringNotifier / SessionAbandonSweeper), KHÔNG ở đây.
public interface IRoadmapLessonService
{
    // GET /roadmaps/{id}/lessons/{lessonId} — mở lesson. theory_content null → gọi AIService sinh (sync)
    // → lưu rồi trả; lần sau đọc DB (lazy, idempotent). AI lỗi → AiServiceException (502). Miễn phí.
    Task<LessonResponse> OpenLessonAsync(
        Guid candidateId, Guid roadmapId, Guid lessonId, CancellationToken ct = default);

    // POST /roadmaps/{id}/lessons/{lessonId}/start — tạo practice session B2C (reserve 1 credit như BC2;
    // hết → 402 KHÔNG tạo session), câu hỏi bám focusCriteria; link lesson Theory→Practicing + mile
    // Pending→InProgress. Đang Practicing/Done → LessonAlreadyStartedException (409, không reserve thêm).
    Task<PracticeSessionResponse> StartLessonAsync(
        Guid candidateId, Guid roadmapId, Guid lessonId, CancellationToken ct = default);

    // POST /roadmaps/{id}/lessons/{lessonId}/retry — LÀM LẠI bài đã hoàn thành để nâng điểm. Cùng
    // giá (1 credit) và cùng đường tạo buổi như /start, câu hỏi SINH MỚI; giữ trọn lịch sử các lần
    // làm trong roadmap_lesson_attempts. Còn Theory / đang Practicing →
    // LessonRetryNotAllowedException (409). Lộ trình đã Completed → mở lại Active + xoá báo cáo chốt.
    Task<PracticeSessionResponse> RetryLessonAsync(
        Guid candidateId, Guid roadmapId, Guid lessonId, CancellationToken ct = default);
}
