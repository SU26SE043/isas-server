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

        /// <summary>
        /// F20 — `sub` của PlatformAdmin đã cấp credit khuyến mãi (chỉ set trên row
        /// <see cref="CreditTransactionReason.PromoGrant"/>). Ref LỎNG → Auth (GEN-2).
        ///
        /// Không có cột này thì quà tặng thủ công là loại credit DUY NHẤT xuất hiện trong ví mà không
        /// truy được nguồn: không qua thanh toán (nên không có <c>order_id</c>), không do luật tự động
        /// (nên không suy ra được từ ngữ cảnh). Cấp credit là in tiền trong hệ thống này — phải ký tên.
        /// </summary>
        public Guid? GrantedBy { get; set; }

        /// <summary>F20 — ghi chú của admin lúc cấp (lý do khuyến mãi / đền bù sự cố).</summary>
        public string? Note { get; set; }

        /// <summary>R8 — khoá idempotency client gửi cho một lần cấp quà; unique theo chủ ví khi khác null.</summary>
        public string? GrantIdempotencyKey { get; set; }

        /// <summary>
        /// R8 — snapshot số dư ngay sau lần cấp. Không đọc số dư hiện tại khi replay: có thể đã phát sinh
        /// giao dịch khác và response retry phải giống chính xác response ban đầu.
        /// </summary>
        public int? GrantRemainingCreditsAfter { get; set; }
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
        FreeGrant,

        /// <summary>
        /// F20 — credit khuyến mãi do PlatformAdmin cấp tay (+N, có <c>granted_by</c>).
        ///
        /// TÁCH khỏi <see cref="Purchase"/> và <see cref="FreeGrant"/> vì ba thứ này trả lời ba câu hỏi
        /// kế toán khác nhau: Purchase = tiền thật đã thu, FreeGrant = suất dùng thử cấp TỰ ĐỘNG lúc tạo
        /// ví (F7), PromoGrant = quà do người quyết định. Gộp quà vào Purchase sẽ bơm khống doanh thu
        /// (F19); gộp vào FreeGrant sẽ làm hỏng phép "ví này đã dùng suất dùng thử chưa".
        /// </summary>
        PromoGrant
    }
}
