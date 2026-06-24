using Isas.InterviewService.Enums;

namespace Isas.InterviewService.Entities;

public class PracticeSession
{
    public Guid Id { get; set; } = Guid.NewGuid();
 
    // Tham chiếu lỏng sang AuthService (candidate) - không FK xuyên service
    public Guid CandidateId { get; set; }
 
    // FK cứng tới FileRecord (B2C: file_records nằm chung interview DB)
    public Guid? CvId { get; set; }
    public Guid? JdId { get; set; }
    public JobCategory JobCategory { get; set; }
 
    public SessionStatus Status { get; set; } = SessionStatus.GeneratingQuestions;
 
    public DateTime CreatedAt { get; set; } = DateTime.UtcNow;
    public DateTime? CompletedAt { get; set; }
 
    // Navigation
    public ICollection<PracticeQuestion> Questions { get; set; } = [];
    public ICollection<PracticeAnswer> Answers { get; set; } = [];
}
