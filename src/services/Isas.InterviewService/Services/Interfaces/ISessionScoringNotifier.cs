namespace Isas.InterviewService.Services.Interfaces;

// Tính điểm tổng có trọng số + phát SessionScored khi session đóng sang Scored (E2).
// Dùng chung 2 nơi session có thể đóng sang Scored: AnswerService (đóng qua callback chấm dần
// khi answer cuối được chấm/failed) và PracticeService (đóng NGAY lúc submit nếu mọi answer đã
// Scored từ trước — nhánh "đóng-ngay" của SubmitSessionAsync).
public interface ISessionScoringNotifier
{
    Task NotifySessionScoredAsync(Guid sessionId, CancellationToken ct = default);

    // PAY-13: session đóng mà KHÔNG có answer nào Scored (mọi answer Failed/Skipped) → KHÔNG tính
    // là buổi chấm được → phát SessionAbandoned (Payment release reservation) thay vì SessionScored
    // (consume). Best-effort như NotifySessionScoredAsync (session đã terminal trong DB, publish lỗi
    // chỉ log). Dùng chung bởi AnswerService (callback chấm dần) + PracticeService (nhánh đóng-ngay submit).
    Task NotifySessionAbandonedAsync(Guid sessionId, string reason, CancellationToken ct = default);
}
