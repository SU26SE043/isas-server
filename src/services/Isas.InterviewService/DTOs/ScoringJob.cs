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

    // E10 — self-consistency: 1 answer publish N job (attempt 1..N). Worker echo attempt_no về
    // callback để .NET lưu theo đúng attempt. Mặc định 1 = 1 lần chấm (hành vi cũ, worker cũ đọc → 1).
    public int AttemptNo { get; set; } = 1;

    // E10 — nhiệt độ chấm cho attempt này (worker set generate_content temperature). Attempt 1 = 0
    // (tái lập); 2..N = Scoring:SelfConsistencyTemperature (dao động thật để đo spread). null → worker
    // dùng mặc định (0) — tương thích worker cũ.
    public double? Temperature { get; set; }

    // Phỏng vấn THÍCH ỨNG — transcript đã transcribe ĐỒNG BỘ khi upload (qua /decide-next). Có giá trị →
    // worker BỎ QUA Whisper, chấm thẳng transcript này (single-source; tiết kiệm N lần Whisper self-
    // consistency E10). null (luồng cũ / re-publish job cũ) → worker tải audio + Whisper như trước.
    public string? Transcript { get; set; }
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

    // E10 — attempt worker vừa chấm (echo từ job). Idempotent theo (attempt_no, rubric_version):
    // gửi lại cùng attempt → thay điểm cũ, không nhân đôi. Mặc định 1 (worker cũ không gửi → 1).
    public int AttemptNo { get; set; } = 1;

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
