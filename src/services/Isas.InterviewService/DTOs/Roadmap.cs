namespace Isas.InterviewService.DTOs;

using Isas.InterviewService.Enums;

// BC12 (D20) — DTO roadmap ôn tập cá nhân hoá B2C.

// POST /roadmaps — cvId optional (parse sẵn ở Files). jobCategory/level bắt buộc (enum sai → 400).
// BC17 — candidate CHỌN nguồn nuôi roadmap thay vì tự gom MỌI buổi Scored:
//   • SessionIds     — buổi luyện đã Scored làm baseline; rỗng/null → roadmap CHUẨN theo level (không gom).
//   • CvAnalysisId   — 1 phân tích CV đã có (BC7) → CHỈ ngữ cảnh prompt (không gọi lại /analyze-cv, KHÔNG trừ credit).
//   • PriorRoadmapId — final_report của 1 roadmap đã hoàn thành (BC15) → CHỈ ngữ cảnh prompt.
//   • Focus          — mô tả tự do muốn AI tập trung vào đâu (≤ 2000 ký tự).
// CvAnalysis + prior-roadmap + focus KHÔNG vào baseline — chỉ là bối cảnh cho AI.
public record CreateRoadmapRequest(
    JobCategory JobCategory,
    RoadmapLevel Level,
    Guid? CvId,
    IReadOnlyList<Guid>? SessionIds = null,   // BC17 — buổi luyện Scored candidate chọn làm baseline
    Guid? CvAnalysisId = null,                // BC17 — cv_analyses (BC7)
    Guid? PriorRoadmapId = null,              // BC17 — roadmaps.final_report (BC15)
    string? Focus = null,                     // BC17 — free-text
    string? Language = null,
    string? Scope = null                      // BE-4 — "Quick"/"Standard"; null → "Standard" (hành vi cũ)
);

// Điểm yếu gửi xuống AIService /generate-roadmap (khớp WeaknessScore: criterionName + percentage).
public record RoadmapWeakness(string CriterionName, decimal Percentage);

// Kết quả AI /generate-roadmap (sync) — chỉ cấu trúc (title/focusCriteria/lessons.title), không điểm.
public record RoadmapGenAiResult(IReadOnlyList<GeneratedMilestone> Milestones);
public record GeneratedMilestone(string Title, IReadOnlyList<string> FocusCriteria, IReadOnlyList<GeneratedLesson> Lessons);
public record GeneratedLesson(string Title);

// { criterionName, deltaPct } — set khi milestone Completed (BC15); BC12 luôn null.
public record MilestoneImprovementResponse(string CriterionName, decimal DeltaPct);

// F15 — kết quả AIService /generate-lesson-theory: markdown + tài liệu học (đã qua allowlist
// tên miền phía AIService). Resources rỗng KHÔNG phải lỗi.
// RAG grounding — CitedChunkIds: id chunk grounding mà AI THẬT SỰ cite (Contract 2). Rỗng khi không
// truyền grounding / AI không cite → lesson ungrounded.
public record LessonTheoryResult(
    string TheoryMarkdown,
    IReadOnlyList<Entities.LessonResource> Resources,
    IReadOnlyList<string>? CitedChunkIds = null);

// F15 — 1 tài liệu học gợi ý trả cho FE. `url` CÓ THỂ NULL vì có chủ đích: link do AI sinh chỉ
// được giữ khi tên miền thuộc allowlist (AIService app/resources.py). FE: có url → render link kèm
// nhãn "chưa kiểm chứng"; không url → chỉ hiện tên (người học tự tra).
public record LessonResourceResponse(
    string Title,
    string Type,          // Doc | Course | Book | Video | Article
    string? Publisher,
    string? Url
);

public record LessonResponse(
    Guid Id,
    int OrderNo,
    string Title,
    string? TheoryContent,   // null khi chưa mở (BC14); list bỏ luôn theoryContent.
    Guid? SessionId,
    string Status,
    IReadOnlyList<LessonResourceResponse> Resources,   // F15 — rỗng khi chưa mở lesson / AI không gợi ý được
    // RAG grounding — nguồn UY TÍN đã cite cho lý thuyết bài học ({chunkId, sourceUrl, sourceTitle}).
    // 3 trạng thái như QuestionResponse.Citations: null = roadmap cũ (chưa precompute); [] = precompute
    // chạy nhưng corpus không phủ → ungrounded; non-empty = grounded. Chỉ surface khi kèm theory.
    IReadOnlyList<Citation>? Citations = null
);

public record MilestoneResponse(
    Guid Id,
    int OrderNo,
    string Title,
    IReadOnlyList<string> FocusCriteria,
    string Status,
    IReadOnlyList<MilestoneImprovementResponse>? Improvement,
    IReadOnlyList<LessonResponse> Lessons
);

public record RoadmapResponse(
    Guid Id,
    string JobCategory,
    string Level,
    string Language,
    Guid? CvId,
    string Status,
    IReadOnlyList<MilestoneResponse> Milestones,   // theo orderNo
    DateTime CreatedAt,
    DateTime? CompletedAt
);

/// <summary>
/// Một dòng trong `GET /roadmaps` (DANH SÁCH). KHÔNG có <c>milestones</c> — khác
/// <see cref="RoadmapResponse"/> của endpoint chi tiết `GET /roadmaps/{id}`, vốn giữ nguyên đủ cây.
///
/// Vì sao bỏ hẳn thay vì trả cây rỗng: list trước đây <c>Include(Milestones).ThenInclude(Lessons)</c>
/// nên payload nhân theo cây (mỗi roadmap × mỗi milestone × mỗi lesson) cho một màn hình chỉ vẽ
/// tiêu đề + ngày + trạng thái. Đã đối chiếu FE (`isas-frontend`): trang danh sách roadmap chỉ đọc
/// id/jobCategory/level/createdAt/status, còn <c>milestones</c> chỉ được đọc ở trang CHI TIẾT (gọi
/// endpoint khác) ⇒ bỏ khỏi list không hỏng gì. Trả <c>[]</c> thì sẽ là nói dối ("roadmap này không
/// có chặng nào"), nên chọn bỏ hẳn key.
///
/// Cần hiển thị "N chặng" trên thẻ danh sách về sau → thêm <c>MilestoneCount</c> project bằng
/// subquery scalar (<c>x.Milestones.Count</c>), KHÔNG quay lại Include cả cây.
/// </summary>
public record RoadmapSummaryResponse(
    Guid Id,
    string JobCategory,
    string Level,
    Guid? CvId,
    string Status,
    DateTime CreatedAt,
    DateTime? CompletedAt
);
