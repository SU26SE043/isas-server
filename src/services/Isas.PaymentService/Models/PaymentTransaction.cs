namespace PaymentService.Models
{
    /// <summary>
    /// Log sự kiện gateway (append-only) — payment.md §payment_transactions. N–1 với orders (1 order có
    /// nhiều sự kiện). <see cref="OrderId"/> nullable: webhook không khớp đơn nào (ping test PayOS /
    /// đơn service khác) vẫn lưu bằng chứng đối soát với order_id null (KHÔNG cộng credit).
    /// </summary>
    public class PaymentTransaction
    {
        public Guid Id { get; set; }
        public Guid? OrderId { get; set; }
        public string Gateway { get; set; } = "payos";
        public string? GatewayTxnId { get; set; }
        public string Status { get; set; } = null!; // success | failed | cancelled
        public string? RawWebhookPayload { get; set; }
        public DateTime CreatedAt { get; set; }

        public Order? Order { get; set; }
    }
}
