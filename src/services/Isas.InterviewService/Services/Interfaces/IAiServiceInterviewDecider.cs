using Isas.InterviewService.DTOs;

namespace Isas.InterviewService.Services.Interfaces;

// Phỏng vấn THÍCH ỨNG — typed HttpClient gọi AIService `/decide-next` (máy-máy, X-Internal-Token).
// Gửi audio key (AIService transcribe đồng bộ) + lịch sử + tiêu chí → nhận hành động kế tiếp + câu hỏi.
public interface IAiServiceInterviewDecider
{
    Task<DecideNextResult> DecideNextAsync(
        string audioObjectKey,
        string jobCategory,
        string currentQuestion,
        IReadOnlyList<DecideTurnDto> history,
        int askedCount,
        int followUpCount,
        int maxQuestions,
        int maxFollowUps,
        IReadOnlyList<DecideCriterionDto> criteria,
        CancellationToken ct = default);
}
