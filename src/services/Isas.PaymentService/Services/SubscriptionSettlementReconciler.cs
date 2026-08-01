using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Options;
using PaymentService.Models;

namespace Isas.PaymentService.Services;

/// <summary>
/// PAY-10 safety net for subscription money: never grants or refunds automatically. It only makes a
/// paid subscription order without its entitlement visible for a PlatformAdmin to reconcile manually.
/// </summary>
public sealed class SubscriptionSettlementReconciler : BackgroundService
{
    private readonly IServiceScopeFactory _scopes;
    private readonly ReconcileSettings _settings;
    private readonly ILogger<SubscriptionSettlementReconciler> _logger;

    public SubscriptionSettlementReconciler(IServiceScopeFactory scopes, IOptions<ReconcileSettings> settings,
        ILogger<SubscriptionSettlementReconciler> logger)
        => (_scopes, _settings, _logger) = (scopes, settings.Value, logger);

    protected override async Task ExecuteAsync(CancellationToken ct)
    {
        await Task.Delay(TimeSpan.FromSeconds(30), ct);
        var interval = TimeSpan.FromSeconds(_settings.ScanIntervalSeconds > 0 ? _settings.ScanIntervalSeconds : 120);
        while (!ct.IsCancellationRequested)
        {
            try { await ScanOnceAsync(ct); }
            catch (Exception ex) { _logger.LogError(ex, "Subscription settlement reconcile failed"); }
            await Task.Delay(interval, ct);
        }
    }

    private async Task ScanOnceAsync(CancellationToken ct)
    {
        if (!_settings.Enabled) return;
        using var scope = _scopes.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<PaymentDbContext>();
        var unpaidEntitlements = await db.Orders.AsNoTracking()
            .Where(o => (o.Kind == OrderKind.SubscriptionPurchase || o.Kind == OrderKind.SubscriptionRenewal)
                        && o.Status == OrderStatus.Paid
                        && !db.Subscriptions.Any(s => s.OrderId == o.Id))
            .Select(o => new { o.Id, o.OwnerType, o.OwnerId, o.PackageId, o.PayosOrderCode, o.PaidAt })
            .ToListAsync(ct);

        foreach (var order in unpaidEntitlements)
            _logger.LogError(
                "Paid subscription order {OrderId} (PayOS {PayosOrderCode}, owner {OwnerType}/{OwnerId}, package {PackageId}, paid {PaidAt}) has no subscription; reconcile manually.",
                order.Id, order.PayosOrderCode, order.OwnerType, order.OwnerId, order.PackageId, order.PaidAt);
    }
}
