namespace Isas.InterviewService.Entities;

public class RubricLevel
{
    public Guid Id { get; set; } = Guid.NewGuid();

    public Guid CriterionId { get; set; }
    public RubricCriterion Criterion { get; set; } = null!;

    // Mức điểm (vd 0,1,2,3,4,5)
    public int Score { get; set; }

    // Mô tả mức điểm này nghĩa là gì
    public string Descriptor { get; set; } = null!;

    // Navigation
    public ICollection<RubricAnchor> Anchors { get; set; } = [];
}