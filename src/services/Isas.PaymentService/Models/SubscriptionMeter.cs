namespace PaymentService.Models;
public class SubscriptionMeter
{
    public Guid SubscriptionId { get; set; }
    public DateTime PeriodStart { get; set; }
    public int UsedCount { get; set; }
    public int ReservedCount { get; set; }
    public DateTime UpdatedAt { get; set; } = DateTime.UtcNow;
    public Subscription? Subscription { get; set; }
}
