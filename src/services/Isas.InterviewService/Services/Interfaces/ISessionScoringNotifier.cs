namespace Isas.InterviewService.Services.Interfaces;

// Tính điểm tổng có trọng số + phát SessionScored khi session đóng sang Scored (E2).
// Dùng chung 2 nơi session có thể đóng sang Scored: AnswerService (đóng qua callback chấm dần
// khi answer cuối được chấm/failed) và PracticeService (đóng NGAY lúc submit nếu mọi answer đã
// Scored từ trước — nhánh "đóng-ngay" của SubmitSessionAsync).
public interface ISessionScoringNotifier
{
    Task NotifySessionScoredAsync(Guid sessionId, CancellationToken ct = default);
}
