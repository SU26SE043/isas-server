using PaymentService.Models;

namespace Isas.PaymentService.Services;

/// <summary>Single ordering primitive; resolution code must not reimplement tier precedence.</summary>
public static class TierPriorityExtensions
{
    public static IOrderedQueryable<Plan> OrderByTierPriority(this IQueryable<Plan> query) =>
        query.OrderByDescending(p => p.Rank).ThenByDescending(p => p.Id);
}
