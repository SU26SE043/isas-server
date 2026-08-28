using Isas.InterviewService.DTOs;
using Isas.InterviewService.Entities;
using Isas.InterviewService.Enums;

namespace Isas.InterviewService.Services.Interfaces;

// BC12 — gọi AIService `/generate-roadmap` (sync HTTP, B2C). AI KHÔNG ghi DB — chỉ trả cấu trúc.
// Lỗi → AiServiceException (→ 502).
public interface IAiServiceRoadmapGenerator
{
    // BC17 — `focus` = mô tả tự do do candidate chọn (optional).
    //
    // 🔴 REC1-B7 — `cvAnalysisSummary`/`priorRoadmapSummary`/`currentLevel` ĐÃ BỊ GỠ khỏi chữ ký
    // này — đừng nối lại. Prompt roadmap chỉ xuất ra CẤU TRÚC (milestone/lesson), mà cả hai nguồn
    // này từng bị chèn kèm câu "không đổi cấu trúc roadmap" — mệnh lệnh tự phủ định. Đo được: nhóm
    // roadmap CÓ chọn CV nêu công nghệ cụ thể ÍT hơn (8,6% vs 12,1%); lộ trình trước chỉ 4/37 đủ
    // điều kiện trên dev, 0 trên môi trường chính (điều kiện quá hẹp để có tác động thật). `cvText`
    // thô cũng đã bị gỡ TRƯỚC bước này (MIS1-B5) với cùng lý do đo được — CV không có chỗ tác động
    // lên một *cấu trúc giáo trình* vốn không đổi theo người.
    //
    // 🔴 ĐÍNH CHÍNH review sau REC1-B7 (bản trước ghi SAI ở đây — mâu thuẫn với comment tại
    // `RoadmapService.CreateAsync`): `req.CvAnalysisId`/`req.PriorRoadmapId` KHÔNG còn được
    // `RoadmapService` kiểm quyền sở hữu gì cả — 2 khối guard 404/403/400 đã gỡ HẲN cùng với
    // `cvAnalysisSummary`/`priorRoadmapSummary`, và cả hai id này KHÔNG được lưu ở bất kỳ đâu
    // (không phải cột nào của `Roadmap` entity — verify bằng grep). Chỉ `req.CvId` còn ý nghĩa
    // THẬT: vẫn lưu xuống `roadmaps.cv_id` (FK Restrict → file_records), và `RoadmapsController.
    // Create` bắt `DbUpdateException` để trả 404 "CV không tồn tại" khi id đó không có row —
    // KHÔNG còn qua đường guard 404 riêng ở service như trước bước này. DTO `CreateRoadmapRequest`
    // GIỮ NGUYÊN 4 trường liên quan (CvId/CvAnalysisId/PriorRoadmapId/CurrentLevel) — expand/
    // contract, dọn ở đợt sau khi frontend ngừng gửi.
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
    // 🔴 `evidence` (BE-5) ĐÃ GỠ khỏi chữ ký NÀY — nó chết sẵn từ MIS1-B5 (còn tham số, không caller
    // nào truyền, đã bỏ khỏi payload). `mistakes` thay thế nó làm nguồn GOM CHỦ ĐỀ (MIS1-B2).
    // `GenerateLessonTheoryAsync` bên dưới VẪN giữ `evidence` — phạm vi gỡ CHỈ áp cho roadmap.
    //
    // `mode` = chế độ lộ trình (`LevelUp` mặc định | `Reinforce` ôn lại). Đổi CHÍNH câu dẫn của
    // prompt (`level` là đích nhắm tới hay mức phải giữ nguyên) nên KHÔNG được quên truyền —
    // mặc định `LevelUp` giữ nguyên hành vi mọi caller cũ.
    Task<RoadmapGenAiResult> GenerateAsync(
        string jobCategory,
        string level,
        IReadOnlyList<RoadmapWeakness>? weaknesses,
        string? focus,                 // BC17 — mô tả tự do
        IReadOnlyList<QuestionTargetCriterionDto>? criteria = null,
        string scope = "Standard",
        RoadmapMode mode = RoadmapMode.LevelUp,
        CancellationToken ct = default,
        // MIS1-B5 — LỖI SAI trích từ buổi luyện đã chấm (RoadmapMistakeLoader), làm nguồn GOM CHỦ
        // ĐỀ (MIS1-B2). Đặt SAU CÙNG (kể cả sau `ct`) — CỐ Ý, để mọi call site cũ (kể cả test dựng
        // đủ tham số cũ) không vỡ chữ ký khi thêm tham số này. Vắng/rỗng ⇒ hành vi cũ, không ràng
        // buộc gì thêm (mẫu `criteria`).
        IReadOnlyList<RoadmapMistake>? mistakes = null);
    Task<RoadmapGenAiResult> GenerateAsync(string jobCategory, string level, IReadOnlyList<RoadmapWeakness>? weaknesses, string? focus, CancellationToken ct, string language, IReadOnlyList<QuestionTargetCriterionDto>? criteria = null, string scope = "Standard", RoadmapMode mode = RoadmapMode.LevelUp, IReadOnlyList<RoadmapMistake>? mistakes = null);

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
        RoadmapMode mode = RoadmapMode.LevelUp,
        CancellationToken ct = default,
        // MIS1-B5 — ≤3 lỗi ĐÚNG bài này (RoadmapLessonService.ResolveLessonMistakes), làm anchor
        // bài giảng (MIS1-B3) + nguồn mistakeReview. Đặt SAU `ct` — cùng lý do đã nêu ở
        // GenerateAsync: không vỡ chữ ký call site cũ. Vắng/rỗng ⇒ hành vi cũ.
        IReadOnlyList<RoadmapMistake>? mistakes = null);
    Task<LessonTheoryResult> GenerateLessonTheoryAsync(string jobCategory, string level, string lessonTitle, IReadOnlyList<string> focusCriteria, IReadOnlyList<string>? weaknesses, IReadOnlyList<GroundingChunk>? grounding, CancellationToken ct, string language, IReadOnlyList<CriterionEvidence>? evidence = null, RoadmapMode mode = RoadmapMode.LevelUp, IReadOnlyList<RoadmapMistake>? mistakes = null);

    // BC15 — nhận xét chung khi roadmap Completed (kết luận chi tiết theo tiến độ tiêu chí). AI KHÔNG ghi DB.
    // best-effort: lỗi → AiServiceException; caller (RoadmapReportService) nuốt → để rỗng/null, KHÔNG chặn Completed.
    Task<RoadmapSummaryAiResult> SummarizeRoadmapAsync(
        string jobCategory,
        string level,
        IReadOnlyList<RoadmapCriteriaProgress> criteriaProgress,
        CancellationToken ct = default);
    Task<RoadmapSummaryAiResult> SummarizeRoadmapAsync(string jobCategory, string level, IReadOnlyList<RoadmapCriteriaProgress> criteriaProgress, CancellationToken ct, string language);
}
