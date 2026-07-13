using Isas.InterviewService.Enums;

namespace Isas.InterviewService.Entities;

// BC12 — milestone của 1 roadmap. order_no UNIQUE(roadmap_id, order_no). focus_criteria = snapshot
// tên tiêu chí trọng tâm (rubric đổi version không hồi tố). improvement set khi Completed (BC15).
public class RoadmapMilestone
{
    public Guid Id { get; set; } = Guid.NewGuid();

    public Guid RoadmapId { get; set; }
    public Roadmap Roadmap { get; set; } = null!;

    public int OrderNo { get; set; }

    public string Title { get; set; } = null!;

    // jsonb string[] — tên tiêu chí milestone này tập trung cải thiện (từ AI /generate-roadmap).
    public List<string> FocusCriteria { get; set; } = [];

    public MilestoneStatus Status { get; set; } = MilestoneStatus.Pending;

    // jsonb? — { criterionName: deltaPct } so baseline / mile trước; set khi Completed (BC15). BC12 null.
    public Dictionary<string, decimal>? Improvement { get; set; }

    public DateTime? CompletedAt { get; set; }

    // Navigation — Cascade theo milestone_id.
    public ICollection<RoadmapLesson> Lessons { get; set; } = [];
}
