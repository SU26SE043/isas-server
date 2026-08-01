using PaymentService.Models;
namespace Isas.PaymentService.DTOs;
public sealed class GrantSubscriptionRequest { public OwnerType OwnerType { get; set; } public Guid OwnerId { get; set; } public Guid PlanId { get; set; } public int DurationDays { get; set; } public DateTime? ActivatedAt { get; set; } public string IdempotencyKey { get; set; } = null!; }
