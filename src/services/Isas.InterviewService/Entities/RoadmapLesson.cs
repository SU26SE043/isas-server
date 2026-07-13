using Isas.InterviewService.Enums;

namespace Isas.InterviewService.Entities;

// BC12 — lesson trong 1 milestone. order_no UNIQUE(milestone_id, order_no).
// theory_content: AI sinh LẦN ĐẦU mở lesson (lazy, BC14) — BC12 tạo với null.
// session_id: session luyện gắn lesson, set khi /start (BC14) — FK Restrict → practice_sessions.
public class RoadmapLesson
{
    public Guid Id { get; set; } = Guid.NewGuid();

    public Guid MilestoneId { get; set; }
    public RoadmapMilestone Milestone { get; set; } = null!;

    public int OrderNo { get; set; }

    public string Title { get; set; } = null!;

    // markdown lý thuyết — null cho tới khi mở lesson (BC14).
    public string? TheoryContent { get; set; }
    public DateTime? TheoryGeneratedAt { get; set; }

    // Ref FK Restrict → practice_sessions (session luyện của lesson) — set khi /start (BC14).
    public Guid? SessionId { get; set; }

    public LessonStatus Status { get; set; } = LessonStatus.Theory;
}
