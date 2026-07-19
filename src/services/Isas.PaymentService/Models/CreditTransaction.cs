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

        /// <summary>
        /// F18 — bút toán GỐC mà bút toán này đảo (chỉ set trên row <see cref="CreditTransactionReason.Refund"/>).
        /// Tự tham chiếu cùng bảng.
        ///
        /// Đây vừa là liên kết đối soát ("khoản −3 này đảo khoản +5 nào"), vừa là **khoá idempotency**:
        /// UNIQUE lọc trên cột này (xem PaymentDbContext) khiến hoàn tiền lần hai cho cùng một bút toán
        /// mua đụng UNIQUE và bị chặn ở tầng DB — cùng lối mà UNIQUE(session_id) chặn double-reserve,
        /// thay vì tin vào một câu check-then-act ở tầng ứng dụng.
        /// </summary>
        public Guid? ReversesTransactionId { get; set; }
        public CreditTransaction? ReversesTransaction { get; set; }
    }

    public enum CreditTransactionReason
    {
        Purchase,
        Consume,
        Refund,

        /// <summary>
        /// F7 — suất dùng thử tặng lúc TẠO ví User (+N, không gắn order/session). Ghi sổ chứ không
        /// cấp "credit không sổ sách": nhờ vậy bất biến `remaining + reserved = Σ delta` vẫn đúng,
        /// nên credit tặng bốc hơi do drift vẫn bị phát hiện y như credit khách trả tiền.
        /// </summary>
        FreeGrant
    }
}
