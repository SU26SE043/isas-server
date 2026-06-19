using System;
using System.Collections.Generic;

namespace Isas.InterviewService.Models;

public partial class PracticeQuestion
{
    public Guid Id { get; set; }

    public Guid SessionId { get; set; }

    public int OrderIndex { get; set; }

    public string Content { get; set; } = null!;

    public DateTime CreatedAt { get; set; }

    public virtual PracticeAnswer? PracticeAnswer { get; set; }

    public virtual PracticeSession Session { get; set; } = null!;
}
