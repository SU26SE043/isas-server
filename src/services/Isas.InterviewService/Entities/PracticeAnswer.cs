using Isas.InterviewService.Enums;

namespace Isas.InterviewService.Entities;

public class PracticeAnswer
{
    public Guid Id { get; set; } = Guid.NewGuid();
 
    public Guid SessionId { get; set; }
    public PracticeSession Session { get; set; } = null!;
 
    public Guid QuestionId { get; set; }
    public PracticeQuestion Question { get; set; } = null!;
 
    // Con trỏ tới SeaweedFS (key/path, không phải URL công khai)
    public string? AudioObjectKey { get; set; }
 
    public string? Transcript { get; set; }
 
    public AnswerStatus Status { get; set; } = AnswerStatus.Uploaded;

    // E10 — self-consistency: chấm N lần, nếu spread điểm giữa các attempt (mỗi tiêu chí) vượt
    // Scoring:VarianceThreshold → gắn cờ này để HR (B2B) / người luyện (B2C) xem lại. Điểm AI
    // = gợi ý (INT-14/15/16), KHÔNG auto coi là điểm cuối. Mặc định false (N=1 → luôn false).
    public bool NeedsReview { get; set; }

    // F13 (FR07) — câu trả lời MẪU mức tối đa cho ĐÚNG câu hỏi này, do CÙNG lượt chấm sinh ra
    // (worker gửi kèm callback → không tốn thêm 1 call AI). null = chưa chấm / LLM không trả.
    // ⚠ KHÁC HẲN RubricLevel.ExampleAnswers: cái kia là anchor ĐẦU VÀO để hiệu chỉnh AI lúc chấm,
    // không bao giờ trả ra cho người dùng (và thực tế luôn rỗng vì không có write path nào).
    // Reset cùng transcript/scores khi upload lại (INT-3) — giữ lại sẽ hiện gợi ý của bài CŨ.
    public string? SampleAnswer { get; set; }

    public int DurationSec { get; set; }

    // ── F11 (FR06) — chỉ số ĐỘ TRÔI CHẢY đo từ mốc thời gian Whisper ────────────────────────
    // Tất cả nullable: null = CHƯA ĐO ĐƯỢC (answer cũ trước F11 · audio rỗng · đường degrade khi
    // /decide-next lỗi), KHÁC HẲN với 0 = "đo ra 0". Phân biệt được hai ca này mới hiển thị đúng:
    // "chưa có dữ liệu" vs "nói 0 từ đệm". Reset cùng transcript/scores khi upload lại (INT-3) —
    // giữ lại là hiện chỉ số của bản ghi âm không còn tồn tại.
    //
    // ⚠ FillerCount là mức TỐI THIỂU (Whisper nuốt bớt từ đệm) — xem DeliveryMetricsDto.

    /// <summary>Âm tiết/phút (tiếng Việt đơn âm tiết).</summary>
    public double? SpeechRateWpm { get; set; }

    public int? FillerCount { get; set; }
    public int? PauseCount { get; set; }
    public double? LongestPauseSec { get; set; }
    public double? SilenceRatio { get; set; }

    /// <summary>Chi tiết từ đệm dạng JSON (<c>{"ừm":3,"kiểu như":1}</c>) để hiện cho người luyện.
    /// Lưu <b>text</b> chứ không phải jsonb: dữ liệu này chỉ để đọc-hiển-thị, không truy vấn theo
    /// khoá bao giờ — chọn jsonb ở đây chỉ tổ rước rủi ro migration (xem F15) mà không được gì.</summary>
    public string? FillerBreakdown { get; set; }

    public DateTime CreatedAt { get; set; } = DateTime.UtcNow;

    // Mốc lần gần nhất job chấm được đẩy lên queue (kể cả republish).
    // null = chưa publish lần nào (publish hụt lúc upload).
    // Republisher đo elapsed theo cột này -> biết answer đang chờ hay đã kẹt thật,
    // tránh đẩy lại answer mà worker vẫn đang chấm (Whisper CPU chậm).
    public DateTime? LastScoringPublishedAt { get; set; }

    // Navigation
    public ICollection<AnswerScore> Scores { get; set; } = [];
}