namespace Isas.InterviewService.Models;

public class PracticeQuestion
{
    public Guid Id { get; set; }
    public Guid SessionId { get; set; }
    public int OrderIndex { get; set; }
    public string Content { get; set; } = default!;
    public DateTime CreatedAt { get; set; }

    public PracticeSession Session { get; set; } = default!;
    public PracticeAnswer? Answer { get; set; }
}