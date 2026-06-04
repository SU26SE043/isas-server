namespace Isas.InterviewService.Models;

public class PracticeAnswer
{  
    public Guid Id { get; set; }
    public Guid QuestionId { get; set; }
    public Guid SessionId { get; set; }
    public string AnswerType { get; set; } = default!;    // text | audio
    public string? TextContent { get; set; }              // text hoặc transcript
    public Guid? AudioFileId { get; set; }
    public decimal? Score { get; set; }
    public string? Feedback { get; set; }
    public DateTime CreatedAt { get; set; }

    public PracticeQuestion Question { get; set; } = default!;
    public FileRecord? AudioFile { get; set; }
    
}