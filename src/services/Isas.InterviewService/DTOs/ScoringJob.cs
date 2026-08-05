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

    // Con dấu ENGINE đã chép ra `Transcript` ở trên. Đi cặp với nó, cùng lý do như DeliveryMetrics:
    // worker bỏ Whisper khi job có Transcript, nên nó KHÔNG tự biết engine nào đã chép — không gửi
    // kèm thì worker không có gì để echo về callback, và con dấu chỉ tồn tại ở đường thích ứng còn
    // đường republisher thì mất. null = job không mang transcript (worker tự chép, tự đóng dấu).
    public string? TranscriptEngine { get; set; }

    // F11 — chỉ số cách nói đo trong CÙNG lượt transcribe đồng bộ đó. PHẢI đi kèm Transcript:
    // worker bỏ Whisper khi job có Transcript, nên nếu không gửi kèm thì buổi THÍCH ỨNG không
    // bao giờ có chỉ số trong khi buổi TĨNH vẫn có — hỏng âm thầm, không lỗi nào nổ.
    // null (luồng tĩnh / adaptive tắt / decide lỗi) → worker tự transcribe rồi tự đo.
    public DeliveryMetricsDto? DeliveryMetrics { get; set; }
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

    // Engine đã chép ra `Transcript` ngay trên (vd "whisper-1" / "gemini-2.5-flash" /
    // "whisper-local-small"). AIService chép qua nhà cung cấp TỪ XA và rơi về Whisper CỤC BỘ khi
    // mạng hỏng ⇒ hai answer cùng buổi có thể dùng hai engine khác nhau, mà chất lượng chữ đi thẳng
    // vào điểm chấm (Whisper small sai 4,2% số từ vs 0,5–0,7% của engine từ xa). Không đóng dấu thì
    // chênh lệch đó vô hình ở xếp hạng B2B (CAMP-10) và ở đo cải thiện roadmap (BC15).
    //
    // 🔴 KHOÁ DÂY CỐ ĐỊNH: `transcriptEngine` (camelCase). AIService gửi đúng tên này. Lệch tên giữa
    // hai ngôn ngữ KHÔNG ném lỗi ở đâu cả — nó chỉ im lặng điền null, và repo đã dính đúng kiểu này
    // 3 lần (`focusCriteria` bị pydantic nuốt · `adaptiveMaxQuestions` vs `maxQuestions` làm mọi gói
    // trả phí nhận trần 0 câu · `promptVersion` suýt để cột NULL vĩnh viễn). Đổi tên field ở đây phải
    // đổi đồng thời phía Python — `TranscriptEngineWireContractTests` khoá hợp đồng này.
    //
    // Nullable + không [Required] (mẫu SampleAnswer/DeliveryMetrics/PromptVersion): worker/image CŨ
    // không gửi vẫn phải chấm được, và một cột kiểm toán TUYỆT ĐỐI không được biến thành đường làm
    // answer Failed (PAY-13: Failed = người luyện mất 1 credit).
    public string? TranscriptEngine { get; set; }

    public int RubricVersion { get; set; }

    // E10 — attempt worker vừa chấm (echo từ job). Idempotent theo (attempt_no, rubric_version):
    // gửi lại cùng attempt → thay điểm cũ, không nhân đôi. Mặc định 1 (worker cũ không gửi → 1).
    public int AttemptNo { get; set; } = 1;

    public List<ScoreItemDto> Scores { get; set; } = [];

    // F13 — câu trả lời mẫu do CÙNG lượt chấm sinh (worker gửi kèm). Nullable + không [Required]:
    // worker/image CŨ không gửi field này vẫn phải chấm được (deploy AIService lệch nhịp .NET là
    // chuyện thường ở đây), và LLM lỡ bỏ field cũng KHÔNG được làm hỏng lượt chấm (PAY-13).
    public string? SampleAnswer { get; set; }

    // F11 — chỉ số cách nói của CHÍNH lượt transcribe đã dùng để chấm. Nullable + không
    // [Required] (mẫu SampleAnswer): worker/image CŨ không gửi vẫn phải chấm được, và chỉ số
    // thiếu KHÔNG được phép làm hỏng lượt chấm (answer Failed = mất credit, PAY-13).
    public DeliveryMetricsDto? DeliveryMetrics { get; set; }

    // BK23 — con dấu phiên bản prompt của CHÍNH lượt chấm này, do worker chụp tại chỗ dựng
    // prompt (`ScoreOutcome.prompt_version`). .NET đóng lên từng dòng answer_scores.
    //
    // Vì sao NHẬN TỪ WORKER thay vì Interview tự đọc `GetPromptVersionStampAsync()` lúc lưu:
    // AIService cache mảnh prompt theo TTL và CỐ Ý fail-open về cache CŨ khi registry lỗi (F21,
    // tầng 3), còn lượt chấm thì bất đồng bộ qua RabbitMQ + có thể bị `StuckAnswerRepublisher`
    // đẩy lại sau hàng giờ. Nên "phiên bản đang trong DB lúc callback về" thường xuyên KHÁC
    // "phiên bản thực sự đã chấm". Con dấu sai còn TỆ HƠN NULL: cả lý do tồn tại của cột là trả
    // lời "hai điểm này có cùng thước đo không" — con dấu nói dối thì nó trả lời sai một cách
    // tự tin, và không ai có cách nào biết.
    //
    // Nullable + không [Required] (mẫu SampleAnswer/DeliveryMetrics): worker/image CŨ không gửi
    // → NULL = "không biết chấm bằng prompt nào", phân biệt được với 0 = "bản mặc định thuần".
    public int? PromptVersion { get; set; }
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
