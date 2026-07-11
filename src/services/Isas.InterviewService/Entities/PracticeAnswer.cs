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

    public int DurationSec { get; set; }
 
    public DateTime CreatedAt { get; set; } = DateTime.UtcNow;

    // Mốc lần gần nhất job chấm được đẩy lên queue (kể cả republish).
    // null = chưa publish lần nào (publish hụt lúc upload).
    // Republisher đo elapsed theo cột này -> biết answer đang chờ hay đã kẹt thật,
    // tránh đẩy lại answer mà worker vẫn đang chấm (Whisper CPU chậm).
    public DateTime? LastScoringPublishedAt { get; set; }

    // Navigation
    public ICollection<AnswerScore> Scores { get; set; } = [];
}