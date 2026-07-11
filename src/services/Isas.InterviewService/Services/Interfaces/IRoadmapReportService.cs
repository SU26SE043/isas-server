using Isas.InterviewService.DTOs;

namespace Isas.InterviewService.Services.Interfaces;

// BC15 (D20) — hoàn tất milestone/roadmap + report roadmap ôn tập B2C.
// - OnLessonDoneAsync: chokepoint lesson Done (gọi từ SessionScoringNotifier) → mọi lesson Done ⇒ milestone
//   Completed + improvement; mọi milestone Completed ⇒ roadmap Completed + snapshot final_report + AI comment.
// - GetReportAsync: GET /roadmaps/{id}/report — Active tính interim on-read; Completed đọc snapshot.
public interface IRoadmapReportService
{
    // Best-effort (caller bọc try/catch): rollup completion khi 1 lesson vừa Scored→Done. Idempotent/absorbing.
    Task OnLessonDoneAsync(Guid sessionId, CancellationToken ct = default);

    // null → 404; khác chủ → UnauthorizedAccessException (403). Active → interim (kết luận rỗng/null);
    // Completed → snapshot roadmaps.final_report (KHÔNG tính lại).
    Task<RoadmapReportResponse?> GetReportAsync(Guid candidateId, Guid roadmapId, CancellationToken ct = default);
}
