using Isas.InterviewService.DTOs;

namespace Isas.InterviewService.Services.Interfaces;

// BC7 — phân tích CV B2C: parse (đọc parsed_text file) → AIService /analyze-cv → lưu cv_analyses.
public interface ICvAnalysisService
{
    // Ném: KeyNotFoundException (404 cvId/jdId không có) · UnauthorizedAccessException (403 khác chủ)
    //      · InvalidOperationException (400 CV/JD không đọc được) · AiServiceException (502 AI lỗi).
    Task<CvAnalysisResponse> AnalyzeAsync(Guid candidateId, CvAnalysisRequest req, CancellationToken ct = default);

    // null → 404; ném UnauthorizedAccessException → 403 (khác chủ).
    Task<CvAnalysisResponse?> GetAsync(Guid candidateId, Guid id, CancellationToken ct = default);

    Task<IReadOnlyList<CvAnalysisResponse>> ListAsync(Guid candidateId, CancellationToken ct = default);
}
