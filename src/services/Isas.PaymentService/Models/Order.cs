namespace PaymentService.Models
{
    public class Order : IHasUpdatedAt
    {
        public Guid Id { get; set; }
        // P2 (D15) — chủ ví theo owner model (Org B2B / User B2C), thay cho user_id cũ. Ref lỏng → Auth
        // (không FK xuyên service, GEN-2). Enum lưu string (GEN-2).
        public OwnerType OwnerType { get; set; }
        public Guid OwnerId { get; set; }
        public OrderKind Kind { get; set; } = OrderKind.CreditPack;
        // P8b: nullable — đơn CreditPack gắn package_id; đơn InvoiceSettlement (tất toán hóa đơn) KHÔNG có
        // package (gắn invoice_id thay thế). Ref lỏng (không FK xuyên service, GEN-2).
        public Guid? PackageId { get; set; }
        public Guid? InvoiceId { get; set; }
        public OrderStatus Status { get; set; }
        // long (amount_vnd bigint — payment.md §DB): VND nguyên có thể vượt trần int (~2,1 tỷ ₫) với
        // pack lớn / hóa đơn postpaid gộp kỳ. int cũ tràn thầm lặng ⇒ số tiền/PayOS lệch.
        public long AmountVnd { get; set; }
        public long PayosOrderCode { get; set; }
        public DateTime ExpiredAt { get; set; }
        public DateTime? PaidAt { get; set; }
        public DateTime CreatedAt { get; set; }

        // ── F18 — hoàn tiền (admin) ──────────────────────────────────────────────────────────────
        // Thời điểm đơn được đánh dấu hoàn. NULL với mọi đơn chưa hoàn.
        public DateTime? RefundedAt { get; set; }
        // `sub` của PlatformAdmin bấm hoàn — ref LỎNG → Auth (không FK xuyên service, GEN-2).
        // Ai hoàn là thông tin đối soát bắt buộc: hoàn tiền là mutation tiền duy nhất không do
        // cổng thanh toán khởi xướng, nên nếu không ghi người thực hiện thì không truy được trách nhiệm.
        public Guid? RefundedBy { get; set; }
        public string? RefundReason { get; set; }
        // Mã giao dịch hoàn của cổng. Hai nguồn: admin NHẬP TAY sau khi chuyển trên dashboard, HOẶC
        // payout_id do lệnh chi tự động sinh ra (xem PayoutId). Vẫn cho nhập tay vì chuyển khoản ngoài
        // luồng cổng là ca có thật (không resolve được ngân hàng đích, vượt trần tự động).
        public string? RefundGatewayRef { get; set; }
        // Thời điểm XÁC NHẬN đã chuyển tiền thật về cho khách (dashboard PayOS / chuyển khoản tay).
        // Tách khỏi RefundedAt: refund lật đơn→Refunded NGAY (nội bộ), còn chuyển tiền bank là bước tay
        // riêng, có thể sau/có thể quên. NULL = "đã đánh dấu hoàn nhưng CHƯA chuyển tiền" (chờ kế toán);
        // có giá trị = "tiền đã đi". Phân biệt dứt điểm cái mà RefundGatewayRef (cho phép chuyển-tay-không-mã)
        // không diễn đạt được. KHÔNG dính tới credit/status — chỉ là mốc đối soát dòng tiền ra.
        public DateTime? RefundSettledAt { get; set; }

        // ── Hoàn tiền tự động qua kênh chi payOS ────────────────────────────────────────────────
        // Khoá idempotency của lệnh chi, sinh MỘT LẦN và ghi xuống đây TRƯỚC khi gọi payOS.
        //
        // ⚠ Đây là cột chống-mất-tiền quan trọng nhất của cả tính năng. payOS nhận diện lệnh trùng
        // theo khoá này; sinh khoá mới ở lần retry nghĩa là payOS coi đó là lệnh MỚI và chuyển tiền
        // LẦN HAI. Vì thế khoá phải bền vững trước lời gọi mạng, không phải biến cục bộ trong hàm.
        public Guid? PayoutIdempotencyKey { get; set; }
        // Id lệnh chi payOS trả về. NULL mà PayoutIdempotencyKey đã có = đã gọi nhưng chưa biết kết quả
        // (timeout/mất mạng) → reconciler tra bằng ReferenceId = order id.
        public string? PayoutId { get; set; }
        // Trạng thái lệnh chi phía ta. Enum lưu string (GEN-2).
        public PayoutStatus? PayoutStatus { get; set; }
        // Vì sao lệnh chi hỏng/bị chặn — để admin biết phải làm gì thay vì thấy một ô trống.
        public string? PayoutFailureReason { get; set; }

        // DB14 — audit: đóng dấu mỗi lần order bị sửa (status flip Cancel/Paid). C# init để insert không phụ
        // thuộc DB default now() (SQLite/EnsureCreated không có now()); DB default now() vẫn có ở Postgres.
        public DateTime UpdatedAt { get; set; } = DateTime.UtcNow;
        public ProductPackage? Package { get; set; }
        // P8b — hóa đơn được tất toán bởi đơn này (kind=InvoiceSettlement). 1 Invoice ── N Order.
        public Invoice? Invoice { get; set; }
        // N–1 (payment.md §payment_transactions): 1 order nhận NHIỀU sự kiện gateway (webhook redeliver /
        // polling / webhook muộn) — log append-only, không ghi đè.
        public ICollection<PaymentTransaction> PaymentTransactions { get; set; } = [];
        public ICollection<CreditTransaction> CreditTransactions { get; set; } = [];
    }
    public enum OrderStatus
    {
        Pending = 1,
        Paid = 2,
        Failed = 3,
        Expired = 4,
        Cancelled = 5,

        /// <summary>
        /// F18 — đơn đã Paid nhưng được PlatformAdmin hoàn tiền. Trạng thái RIÊNG chứ không tái dùng
        /// <see cref="Cancelled"/>: Cancelled nghĩa là "đơn chết trước khi có tiền" (huỷ lúc Pending),
        /// gộp hai thứ lại thì báo cáo doanh thu không phân biệt được "chưa bao giờ thu" với "đã thu
        /// rồi trả lại", mà đó đúng là hai con số kế toán khác nhau.
        ///
        /// ⚠ Đây là NGOẠI LỆ có chủ đích của PAY-10 (đơn terminal bất biến): PAY-10 sinh ra để chặn
        /// cổng thanh toán tự lật trạng thái đơn (webhook muộn cộng credit lần hai). Hoàn tiền là hành
        /// động NGƯỜI thực hiện, có audit (refunded_by/refund_reason), và mọi guard tự động trong
        /// service đều bám `status == Pending` (webhook · polling · sweeper hết hạn) ⇒ đơn Refunded
        /// không bị đường tự động nào chạm vào, kể cả webhook tới muộn.
        /// </summary>
        Refunded = 6,
    }

    /// <summary>
    /// Trạng thái lệnh chi hoàn tiền phía ta — KHÔNG phải bản sao 1-1 state của payOS.
    ///
    /// <para>Gộp <c>Received</c>/<c>Processing</c> của payOS thành <see cref="InFlight"/> vì với ta
    /// hai cái đó cùng nghĩa "tiền chưa chắc đã đi, đừng đóng dấu, hỏi lại sau". Phân biệt chúng chỉ
    /// tạo cơ hội cho ai đó viết nhầm một nhánh coi <c>Processing</c> là xong.</para>
    /// </summary>
    public enum PayoutStatus
    {
        /// <summary>Đã gửi lệnh (hoặc đã gọi mà chưa rõ kết quả) — chờ reconciler xác định.</summary>
        InFlight = 1,

        /// <summary>payOS xác nhận đã chuyển xong. Chỉ trạng thái NÀY mới được đóng dấu refund_settled_at.</summary>
        Succeeded = 2,

        /// <summary>
        /// payOS báo hỏng (Failed/Cancelled), hoặc ta tự chặn (tên người nhận không khớp).
        /// KHÔNG tự thử lại — chuyển tiền hỏng cần người xem, vì lý do hỏng thường là dữ liệu đích sai.
        /// </summary>
        Failed = 3
    }

    /// <summary>
    /// Loại đơn (payment.md §DB orders — kind varchar(20)). P2 chỉ dùng <see cref="CreditPack"/>
    /// (mua pack prepaid); các giá trị còn lại theo doc để khớp enum tài liệu — phase 2 (postpaid/subscription).
    /// </summary>
    public enum OrderKind
    {
        CreditPack,
        InvoiceSettlement,
        SubscriptionPurchase,
        SubscriptionRenewal
    }
}
