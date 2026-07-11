namespace Isas.PaymentService.DTOs
{
    /// <summary>
    /// E7: shape của event <c>SessionScored</c> nhận từ InterviewService qua RabbitMQ
    /// (exchange <c>interview.events</c> topic, routing key <c>session.scored</c> — E2).
    /// Bản sao CỤC BỘ trong PaymentService — KHÔNG tham chiếu thẳng code/DLL InterviewService
    /// (GEN-2: không FK/dependency xuyên service, ref lỏng bằng Guid). Field khớp
    /// <c>Isas.InterviewService.DTOs.SessionScoredEvent</c>.
    ///
    /// Payment chỉ dùng <see cref="SessionId"/> để <c>ConsumeAsync</c> (P5) — chủ ví lấy từ
    /// reservation, không tin owner trong event; các field còn lại giữ nguyên để faithful với
    /// hợp đồng event (áp cả B2B lẫn B2C: consume cho mọi session Scored — payment.md §Tiêu credit).
    /// </summary>
    public class SessionScoredMessage
    {
        public Guid SessionId { get; set; }

        // null = B2C; Payment consume cả B2B lẫn B2C (khác Campaign chỉ ranking B2B).
        public Guid? CampaignId { get; set; }

        public Guid CandidateId { get; set; }

        public decimal TotalScore { get; set; }

        public DateTime ScoredAt { get; set; }
    }
}
