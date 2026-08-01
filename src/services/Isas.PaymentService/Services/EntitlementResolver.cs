using Isas.PaymentService.DTOs;
using Microsoft.EntityFrameworkCore;
using PaymentService.Models;

namespace Isas.PaymentService.Services;

public sealed class EntitlementResolver
{
    private readonly PaymentDbContext _db;
    public EntitlementResolver(PaymentDbContext db) => _db = db;

    public async Task<EntitlementSet> ResolveAsync(OwnerType ownerType, Guid ownerId, CancellationToken ct = default)
    {
        var now = DateTime.UtcNow;
        var sub = await _db.Subscriptions.AsNoTracking()
            .Where(s => s.OwnerType == ownerType && s.OwnerId == ownerId)
            .ActiveAt(now)
            .OrderByTierPriority()
            .FirstOrDefaultAsync(ct);
        if (sub is not null) return new EntitlementSet
        {
            Source = "resolved", SubscriptionId = sub.Id, Audience = sub.Audience,
            TierCode = sub.TierCode, TierRank = sub.TierRank, InterviewFunding = sub.InterviewFunding,
            MonthlyQuota = sub.MonthlyQuota, EntitlementSnapshot = sub.EntitlementSnapshot
        };

        var audience = ownerType == OwnerType.Org ? PlanAudience.B2B : PlanAudience.B2C;
        var freeCode = audience == PlanAudience.B2B ? "starter" : "free";
        var free = await _db.Plans.AsNoTracking().SingleAsync(p => p.Audience == audience && p.Code == freeCode, ct);
        return new EntitlementSet
        {
            Audience = audience, TierCode = free.Code, TierRank = free.Rank,
            InterviewFunding = free.InterviewFunding, MonthlyQuota = free.MonthlyQuota,
            EntitlementSnapshot = free.EntitlementsJson
        };
    }
}
