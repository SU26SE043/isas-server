namespace Isas.InterviewService.DTOs;

using Isas.InterviewService.Enums;

// BC12 (D20) — DTO roadmap ôn tập cá nhân hoá B2C.

// POST /roadmaps — cvId optional (parse sẵn ở Files). jobCategory/level bắt buộc (enum sai → 400).
public record CreateRoadmapRequest(
    JobCategory JobCategory,
    RoadmapLevel Level,
    Guid? CvId
);

// Điểm yếu gửi xuống AIService /generate-roadmap (khớp WeaknessScore: criterionName + percentage).
public record RoadmapWeakness(string CriterionName, decimal Percentage);

// Kết quả AI /generate-roadmap (sync) — chỉ cấu trúc (title/focusCriteria/lessons.title), không điểm.
public record RoadmapGenAiResult(IReadOnlyList<GeneratedMilestone> Milestones);
public record GeneratedMilestone(string Title, IReadOnlyList<string> FocusCriteria, IReadOnlyList<GeneratedLesson> Lessons);
public record GeneratedLesson(string Title);

// { criterionName, deltaPct } — set khi milestone Completed (BC15); BC12 luôn null.
public record MilestoneImprovementResponse(string CriterionName, decimal DeltaPct);

public record LessonResponse(
    Guid Id,
    int OrderNo,
    string Title,
    string? TheoryContent,   // null khi chưa mở (BC14); list bỏ luôn theoryContent.
    Guid? SessionId,
    string Status
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
    Guid? CvId,
    string Status,
    IReadOnlyList<MilestoneResponse> Milestones,   // theo orderNo
    DateTime CreatedAt,
    DateTime? CompletedAt
);
