namespace PaymentService.Models
{
    public class PaymentTransaction
    {
        public Guid Id { get; set; }
        public Guid OrderId { get; set; }
        public string Gateway { get; set; } = "payos";
        public string? GatewayTxnId { get; set; }
        public string Status { get; set; } = null!; // success | failed | cancelled
        public string? RawWebhookPayload { get; set; }
        public DateTime CreatedAt { get; set; }

        public Order Order { get; set; } = null!;
    }
}
