namespace Isas.InterviewService.DTOs;

public class ScoringJob
{
    public Guid AnswerId { get; set; }
    public Guid SessionId { get; set; }
    public Guid QuestionId { get; set; }
    public string AudioObjectKey { get; set; } = null!;

    // Các field worker Python cần để chấm theo rubric:
    public string QuestionContent { get; set; } = null!;
    public string JobCategory { get; set; } = null!;
    public int RubricVersion { get; set; }
    public List<ScoringCriterionDto> Criteria { get; set; } = [];
}

public class ScoringCriterionDto
{
    public Guid CriterionId { get; set; }
    public string Name { get; set; } = null!;
    public string? Description { get; set; }
    public int MaxScore { get; set; }
    public decimal Weight { get; set; }
}

public class AnswerScoreCallbackRequest
{
    public string Transcript { get; set; } = "";
    public int RubricVersion { get; set; }
    public List<ScoreItemDto> Scores { get; set; } = [];
}

public class ScoreItemDto
{
    public Guid CriterionId { get; set; }
    public decimal Score { get; set; }
    public string? Reasoning { get; set; }
}

// Worker gọi khi chấm thất bại vĩnh viễn (audio hỏng / LLM output không hợp lệ).
public class AnswerFailedCallbackRequest
{
    public string? Reason { get; set; }
}
