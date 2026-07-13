namespace Isas.PaymentService.DTOs
{
    /// <summary>
    /// E7: shape của event <c>SessionAbandoned</c> nhận từ InterviewService qua RabbitMQ
    /// (exchange <c>interview.events</c> topic, routing key <c>session.abandoned</c> — E3).
    /// Bản sao CỤC BỘ trong PaymentService (GEN-2 — không dependency xuyên service). Field khớp
    /// <c>Isas.InterviewService.DTOs.SessionAbandonedEvent</c>.
    ///
    /// Payment nghe để <c>ReleaseAsync</c> (P6) — nhả chỗ giữ khi bài bỏ ngang/lỗi. Chỉ dùng
    /// <see cref="SessionId"/>; <see cref="Reason"/> giữ để log/audit.
    /// </summary>
    public class SessionAbandonedMessage
    {
        public Guid SessionId { get; set; }

        public Guid? CampaignId { get; set; }

        public Guid CandidateId { get; set; }

        // Lý do bỏ ngang (log/audit). Hiện chỉ "expired_no_answer".
        public string Reason { get; set; } = string.Empty;

        public DateTime AbandonedAt { get; set; }
    }
}
