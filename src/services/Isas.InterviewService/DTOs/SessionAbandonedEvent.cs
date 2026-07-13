namespace Isas.InterviewService.DTOs;

// Message phát lên RabbitMQ khi session InProgress quá hạn mà KHÔNG có câu trả lời nào (E3 —
// interview.md §State machine + §Sự kiện phát ra). Người nghe: Payment (release reservation).
// Shape mirror SessionScoredEvent (trừ điểm, cộng lý do bỏ ngang).
// Nhánh ≥1 answer (quá hạn -> auto-submit -> SessionScored) là task I2, chưa build.
public class SessionAbandonedEvent
{
    public Guid SessionId { get; set; }
    public Guid? CampaignId { get; set; }
    public Guid CandidateId { get; set; }

    // Lý do bỏ ngang, cho Payment log/audit. Hiện chỉ 1 giá trị: "expired_no_answer".
    public string Reason { get; set; } = string.Empty;

    public DateTime AbandonedAt { get; set; }
}
