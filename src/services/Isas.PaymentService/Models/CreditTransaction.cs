namespace PaymentService.Models
{
    /// <summary>
    /// Sổ cái credit (append-only) — Purchase (+pack) / Consume (−1/lượt khi Scored) / Refund.
    /// P1: chỉ schema. order_id = FK cùng-service → orders (nullable — Consume không gắn order).
    /// session_id = ref LỎNG → InterviewService (không FK xuyên service, GEN-2).
    /// </summary>
    public class CreditTransaction
    {
        public Guid Id { get; set; }
        public OwnerType OwnerType { get; set; }
        public Guid OwnerId { get; set; }
        public Guid? OrderId { get; set; }
        public Guid? SessionId { get; set; }
        public int Delta { get; set; }
        public CreditTransactionReason Reason { get; set; }
        public DateTime CreatedAt { get; set; }
        public Order? Order { get; set; }
    }

    public enum CreditTransactionReason
    {
        Purchase,
        Consume,
        Refund
    }
}
