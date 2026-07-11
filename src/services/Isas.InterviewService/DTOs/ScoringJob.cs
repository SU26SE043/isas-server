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

    // E9 — mức neo (score→descriptor) để AI CHỌN mức khớp thay vì tự bịa thang.
    // Nguồn: rubric_levels nếu có; nếu không → dải mặc định 0..maxScore sinh tại Interview.
    public List<ScoringLevelDto> Levels { get; set; } = [];

    // E9 (optional) — câu trả lời mẫu neo cho từng mức (chỉ có khi rubric_levels khai anchor).
    public List<ScoringAnchorDto>? Anchors { get; set; }
}

public class ScoringLevelDto
{
    public int Score { get; set; }
    public string Descriptor { get; set; } = null!;
}

public class ScoringAnchorDto
{
    public int Score { get; set; }
    public string ExampleAnswer { get; set; } = null!;
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

    // E9 — mức worker chọn (= Score theo hợp đồng). Optional/nullable: worker cũ không gửi
    // vẫn hợp lệ (không phá client). C# guard sẽ snap/validate theo mức của tiêu chí.
    public int? LevelMatched { get; set; }

    public string? Reasoning { get; set; }
}

// Worker gọi khi chấm thất bại vĩnh viễn (audio hỏng / LLM output không hợp lệ).
public class AnswerFailedCallbackRequest
{
    public string? Reason { get; set; }
}
