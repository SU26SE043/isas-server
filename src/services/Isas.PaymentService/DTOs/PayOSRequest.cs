namespace Isas.PaymentService.DTOs
{
    public class PaymentWebhookRequest
    {
        public long OrderCode { get; set; }

        public string TransactionId { get; set; } = null!;

        public int Amount { get; set; }

        public bool IsSuccess { get; set; }

        public string RawPayload { get; set; } = null!;
    }
}
