using Isas.InterviewService.DTOs;

namespace Isas.InterviewService.Services.Interfaces;

// BC12 — gọi AIService `/generate-roadmap` (sync HTTP, B2C). AI KHÔNG ghi DB — chỉ trả cấu trúc.
// Lỗi → AiServiceException (→ 502).
public interface IAiServiceRoadmapGenerator
{
    Task<RoadmapGenAiResult> GenerateAsync(
        string jobCategory,
        string level,
        IReadOnlyList<RoadmapWeakness>? weaknesses,
        string? cvText,
        CancellationToken ct = default);

    // BC14 — sinh lý thuyết lesson (lazy, sync) khi mở lesson lần đầu. Trả markdown; AI KHÔNG ghi DB.
    // Lỗi → AiServiceException (→ 502; mở lại được vì chưa lưu).
    Task<string> GenerateLessonTheoryAsync(
        string jobCategory,
        string level,
        string lessonTitle,
        IReadOnlyList<string> focusCriteria,
        IReadOnlyList<string>? weaknesses,
        CancellationToken ct = default);
}
