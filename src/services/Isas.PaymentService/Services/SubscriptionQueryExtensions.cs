using Microsoft.EntityFrameworkCore;
using PaymentService.Models;

namespace Isas.PaymentService.Services;

/// <summary>Canonical active-window and deterministic effective-tier ordering for subscriptions.</summary>
internal static class SubscriptionQueryExtensions
{
    internal static IQueryable<Subscription> ActiveAt(this IQueryable<Subscription> query, DateTime now) =>
        query.Where(s => s.Status == SubscriptionStatus.Active && s.ActivatedAt <= now && s.ExpiresAt > now);

    internal static IOrderedQueryable<Subscription> OrderByTierPriority(this IQueryable<Subscription> query) =>
        query.OrderByDescending(s => s.TierRank)
             .ThenByDescending(s => s.ExpiresAt)
             .ThenByDescending(s => s.Id);
}
