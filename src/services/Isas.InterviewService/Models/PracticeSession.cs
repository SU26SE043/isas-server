namespace Isas.InterviewService.Models;

public class PracticeSession
{
    public Guid Id { get; set; }
    public Guid UserId { get; set; }
    public string JobCategory { get; set; } = default!;   // BA | BE | FE
    public string Status { get; set; } = "draft";         // draft | in_progress | submitted | scored | failed | abandoned
    public Guid? CvFileId { get; set; }
    public string? JdText { get; set; }
    public decimal? TotalScore { get; set; }
    public string? Feedback { get; set; }
    public DateTime CreatedAt { get; set; }
    public DateTime? SubmittedAt { get; set; }
    public DateTime? ScoredAt { get; set; }

    public FileRecord? CvFile { get; set; }
    public ICollection<PracticeQuestion> Questions { get; set; } = new List<PracticeQuestion>();
}