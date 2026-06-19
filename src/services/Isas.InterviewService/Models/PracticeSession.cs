using System;
using System.Collections.Generic;

namespace Isas.InterviewService.Models;

public partial class PracticeSession
{
    public Guid Id { get; set; }

    public Guid UserId { get; set; }

    public string JobCategory { get; set; } = null!;

    public string Status { get; set; } = null!;

    public Guid? CvFileId { get; set; }

    public string? JdText { get; set; }

    public decimal? TotalScore { get; set; }

    public string? Feedback { get; set; }

    public DateTime CreatedAt { get; set; }

    public DateTime? SubmittedAt { get; set; }

    public DateTime? ScoredAt { get; set; }

    public virtual FileRecord? CvFile { get; set; }

    public virtual ICollection<PracticeQuestion> PracticeQuestions { get; set; } = new List<PracticeQuestion>();
}
