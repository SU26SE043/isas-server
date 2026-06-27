namespace Isas.InterviewService.Entities;

public class PracticeQuestion
{
    public Guid Id { get; set; } = Guid.NewGuid();
 
    public Guid SessionId { get; set; }
    public PracticeSession Session { get; set; } = null!;
 
    public int OrderNo { get; set; }
    public string Content { get; set; } = null!;
    public int TimeLimitSec { get; set; }
 
    public DateTime CreatedAt { get; set; } = DateTime.UtcNow;
 
    // Navigation - mỗi câu hỏi có tối đa 1 answer (business rule)
    public PracticeAnswer? Answer { get; set; }
}