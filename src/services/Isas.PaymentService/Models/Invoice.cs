namespace PaymentService.Models
{
    /// <summary>
    /// Hóa đơn postpaid — P8b (payment.md §Invoice + §Postpaid chốt kỳ). CHỈ Org (owner_type=Org):
    /// cuối kỳ snapshot <c>period_usage</c> → hóa đơn <c>Issued</c> (<c>interview_count × unit_price</c>) →
    /// reset period_usage=0 (1 transaction). Org tất toán qua PayOS (Order kind=InvoiceSettlement) →
    /// webhook Paid → <c>Issued→Paid</c>. 1 Invoice ── N Order (retry tất toán được — orders.invoice_id).
    /// </summary>
    public class Invoice
    {
        public Guid Id { get; set; }
        // Chủ ví (payment.md dùng owner_type/owner_id đồng bộ credit_accounts/orders — invoice CHỈ Org).
        public OwnerType OwnerType { get; set; } = OwnerType.Org;
        public Guid OwnerId { get; set; }
        // Tham chiếu ví đã chốt kỳ (ref lỏng — không FK xuyên service, nhưng cùng DB nên gắn được).
        public Guid? AccountId { get; set; }
        public DateTime PeriodStart { get; set; }
        public DateTime PeriodEnd { get; set; }
        public int InterviewCount { get; set; }          // = snapshot period_usage lúc chốt
        public decimal UnitPrice { get; set; }           // đơn giá 1 lượt (config Billing:UnitPrice lúc chốt)
        public decimal Amount { get; set; }              // = InterviewCount × UnitPrice
        public InvoiceStatus Status { get; set; } = InvoiceStatus.Issued;
        public DateTime CreatedAt { get; set; }

        /// <summary>F23/BK24 — hạn tất toán = periodEnd + Billing:InvoiceDueDays (snapshot lúc lập).</summary>
        public DateTime? DueAt { get; set; }

        /// <summary>F23/BK24 — set khi webhook PayOS xác nhận Paid (WebhookService, cùng ExecuteUpdate với Status).</summary>
        public DateTime? PaidAt { get; set; }

        // 1 Invoice ── N Order (orders.invoice_id) — N lần tất toán/retry cùng 1 hóa đơn (payment.md §DB).
        public ICollection<Order> Orders { get; set; } = [];
    }

    public enum InvoiceStatus
    {
        Issued,
        Paid,
        Overdue,
        Void
    }
}
