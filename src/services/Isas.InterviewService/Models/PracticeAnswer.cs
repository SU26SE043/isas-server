using System;
using System.Collections.Generic;

namespace Isas.InterviewService.Models;

public partial class PracticeAnswer
{
    public Guid Id { get; set; }

    public Guid QuestionId { get; set; }

    public Guid SessionId { get; set; }

    public string AnswerType { get; set; } = null!;

    public string? TextContent { get; set; }

    //public Guid? AudioFileId { get; set; }

    public decimal? Score { get; set; }

    public string? Feedback { get; set; }

    public DateTime CreatedAt { get; set; }

    public virtual ICollection<AnswerAudio>? AnswerAudios { get; set; }

    public virtual PracticeQuestion Question { get; set; } = null!;
}
