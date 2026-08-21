using Isas.InterviewService.DTOs;

namespace Isas.InterviewService.Services.Interfaces;

// BC12 — gọi AIService `/generate-roadmap` (sync HTTP, B2C). AI KHÔNG ghi DB — chỉ trả cấu trúc.
// Lỗi → AiServiceException (→ 502).
public interface IAiServiceRoadmapGenerator
{
    // BC17 — focus/cvAnalysisSummary/priorRoadmapSummary = ngữ cảnh thêm do candidate chọn (đều optional).
    //
    // BE-1 — `criteria` = tiêu chí năng lực THẬT của (jobCategory, language) này (cùng shape
    // `QuestionTargetCriterionDto` dùng cho chấm-theo-phạm-vi). AIService chỉ cho model chọn
    // `focusCriteria` bằng cách sao chép NGUYÊN VĂN tên trong tập này — vắng/rỗng ⇒ hành vi cũ
    // (không ràng buộc gì thêm, model tự đặt tên). Đo trên production: chỉ 7% `focusCriteria` khớp
    // tên tiêu chí thật khi thiếu tham số này.
    //
    // BE-4 — `scope` = độ dài roadmap candidate CHỌN ("Quick"/"Standard", xem
    // `RoadmapService.ValidateScope`). Mặc định "Standard" giữ hành vi client cũ chưa gửi field.
    //
    // BE-5 — `evidence` = Reasoning (E11) của answer điểm THẤP NHẤT cho tiêu chí yếu, đã tải + cắt
    // trần sẵn (xem `RoadmapEvidenceLoader`). Chẩn đoán hành vi cụ thể thay vì chỉ % trừu tượng.
    Task<RoadmapGenAiResult> GenerateAsync(
        string jobCategory,
        string level,
        IReadOnlyList<RoadmapWeakness>? weaknesses,
        string? cvText,
        string? focus,                 // BC17 — mô tả tự do
        string? cvAnalysisSummary,     // BC17 — tóm tắt từ cv_analyses (BC7)
        string? priorRoadmapSummary,   // BC17 — tóm tắt từ final_report roadmap trước (BC15)
        IReadOnlyList<QuestionTargetCriterionDto>? criteria = null,
        string scope = "Standard",
        IReadOnlyList<CriterionEvidence>? evidence = null,
        CancellationToken ct = default);
    Task<RoadmapGenAiResult> GenerateAsync(string jobCategory, string level, IReadOnlyList<RoadmapWeakness>? weaknesses, string? cvText, string? focus, string? cvAnalysisSummary, string? priorRoadmapSummary, CancellationToken ct, string language, IReadOnlyList<QuestionTargetCriterionDto>? criteria = null, string scope = "Standard", IReadOnlyList<CriterionEvidence>? evidence = null);

    // BC14 — sinh lý thuyết lesson (lazy, sync) khi mở lesson lần đầu. AI KHÔNG ghi DB.
    // F15 — trả kèm TÀI LIỆU HỌC (cùng 1 lần gọi, không thêm round-trip AI); danh sách rỗng là
    // HỢP LỆ (AI không gợi ý được, hoặc mọi link bị allowlist tên miền loại phía AIService).
    // RAG grounding — grounding[] (snapshot precompute từ roadmap_lessons.grounding_refs) → AIService chèn
    // block "TÀI LIỆU THAM CHIẾU UY TÍN" + trả CitedChunkIds (Contract 2). null/rỗng → ungrounded (vẫn sinh).
    // Lỗi → AiServiceException (→ 502; mở lại được vì chưa lưu).
    // BE-5 — `evidence`, cùng shape/lý do như overload GenerateAsync ở trên.
    Task<LessonTheoryResult> GenerateLessonTheoryAsync(
        string jobCategory,
        string level,
        string lessonTitle,
        IReadOnlyList<string> focusCriteria,
        IReadOnlyList<string>? weaknesses,
        IReadOnlyList<GroundingChunk>? grounding = null,
        IReadOnlyList<CriterionEvidence>? evidence = null,
        CancellationToken ct = default);
    Task<LessonTheoryResult> GenerateLessonTheoryAsync(string jobCategory, string level, string lessonTitle, IReadOnlyList<string> focusCriteria, IReadOnlyList<string>? weaknesses, IReadOnlyList<GroundingChunk>? grounding, CancellationToken ct, string language, IReadOnlyList<CriterionEvidence>? evidence = null);

    // BC15 — nhận xét chung khi roadmap Completed (kết luận chi tiết theo tiến độ tiêu chí). AI KHÔNG ghi DB.
    // best-effort: lỗi → AiServiceException; caller (RoadmapReportService) nuốt → để rỗng/null, KHÔNG chặn Completed.
    Task<RoadmapSummaryAiResult> SummarizeRoadmapAsync(
        string jobCategory,
        string level,
        IReadOnlyList<RoadmapCriteriaProgress> criteriaProgress,
        CancellationToken ct = default);
    Task<RoadmapSummaryAiResult> SummarizeRoadmapAsync(string jobCategory, string level, IReadOnlyList<RoadmapCriteriaProgress> criteriaProgress, CancellationToken ct, string language);
}
