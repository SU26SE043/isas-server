using Isas.InterviewService.DTOs;

namespace Isas.InterviewService.Services.Interfaces;

// BC7 — gọi AIService `/analyze-cv` (sync HTTP, B2C). Lỗi → AiServiceException (→ 502).
public interface IAiServiceCvAnalyzer
{
    Task<CvAnalysisAiResult> AnalyzeAsync(
        string jobCategory,
        string cvText,
        string? jdText,
        CancellationToken ct = default,
        IReadOnlyList<CvRequirementInput>? mustHave = null,
        IReadOnlyList<CvRequirementInput>? niceToHave = null);

    Task<(IReadOnlyList<JdRequirementSuggestion> MustHave,
          IReadOnlyList<JdRequirementSuggestion> NiceToHave)> SuggestJdRequirementsAsync(
        string jobCategory,
        string jdText,
        IReadOnlyList<GroundingChunk>? grounding,
        CancellationToken ct = default);
}
