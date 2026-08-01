namespace PaymentService.Models;
public class SubscriptionEvent
{
    public Guid Id { get; set; }
    public Guid SubscriptionId { get; set; }
    public string EventType { get; set; } = null!;
    public string Payload { get; set; } = "{}";
    public DateTime CreatedAt { get; set; }
    public Subscription? Subscription { get; set; }
}
