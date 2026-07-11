using Isas.InterviewService.DTOs;

namespace Isas.InterviewService.Services.Interfaces;

// BC10 — gọi AIService `/summarize-session` (sync HTTP, B2C). AI KHÔNG ghi DB — chỉ trả nhận xét text.
// Best-effort: caller (SessionScoringNotifier) bọc try/catch; lỗi → AiServiceException, KHÔNG chặn Scored.
public interface IAiServiceSessionSummarizer
{
    Task<string> SummarizeAsync(
        string jobCategory,
        decimal overallScore,
        IReadOnlyList<SessionSummaryCriterion> criteriaScores,
        CancellationToken ct = default);
}
