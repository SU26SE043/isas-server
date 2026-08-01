using PaymentService.Models;

namespace Isas.PaymentService.DTOs;

public sealed class EntitlementSet
{
    public string Source { get; init; } = "free-default";
    public Guid? SubscriptionId { get; init; }
    public PlanAudience Audience { get; init; }
    public string TierCode { get; init; } = null!;
    public int TierRank { get; init; }
    public InterviewFunding InterviewFunding { get; init; }
    public int? MonthlyQuota { get; init; }
    public string EntitlementSnapshot { get; init; } = "{}";
}
