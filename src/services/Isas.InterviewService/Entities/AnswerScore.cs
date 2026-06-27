namespace Isas.InterviewService.Entities;

public class AnswerScore
{
    public Guid Id { get; set; } = Guid.NewGuid();
 
    public Guid AnswerId { get; set; }
    public PracticeAnswer Answer { get; set; } = null!;
 
    public Guid CriterionId { get; set; }
    public RubricCriterion Criterion { get; set; } = null!;
 
    // Mặc định 1 - mở đường self-consistency (chấm nhiều lần) sau này
    public int AttemptNo { get; set; } = 1;
 
    public decimal Score { get; set; }
    public string? Reasoning { get; set; }
 
    // Điểm gắn với phiên bản rubric lúc chấm (sửa rubric không làm loạn điểm cũ)
    public int RubricVersion { get; set; }
 
    public DateTime CreatedAt { get; set; } = DateTime.UtcNow;
}