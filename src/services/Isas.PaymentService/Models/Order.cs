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
        // DB14 — audit: đóng dấu mỗi lần order bị sửa (status flip Cancel/Paid). C# init để insert không phụ
        // thuộc DB default now() (SQLite/EnsureCreated không có now()); DB default now() vẫn có ở Postgres.
        public DateTime UpdatedAt { get; set; } = DateTime.UtcNow;
        public ProductPackage? Package { get; set; }
        // P8b — hóa đơn được tất toán bởi đơn này (kind=InvoiceSettlement). 1 Invoice ── N Order.
        public Invoice? Invoice { get; set; }
        // N–1 (payment.md §payment_transactions): 1 order nhận NHIỀU sự kiện gateway (webhook redeliver /
        // polling / webhook muộn) — log append-only, không ghi đè.
        public ICollection<PaymentTransaction> PaymentTransactions { get; set; } = [];
        public Subscription? Subscription { get; set; }
        public ICollection<CreditTransaction> CreditTransactions { get; set; } = [];
    }
    public enum OrderStatus
    {
        Pending = 1,
        Paid = 2,
        Failed = 3,
        Expired = 4,
        Cancelled = 5,
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
