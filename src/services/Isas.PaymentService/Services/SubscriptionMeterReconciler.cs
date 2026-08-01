using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Options;
using PaymentService.Models;

namespace Isas.PaymentService.Services;

/// <summary>Repairs only subscription meter reservations; it deliberately never touches credit accounts.</summary>
public sealed class SubscriptionMeterReconciler : BackgroundService
{
    private readonly IServiceScopeFactory _scopes;
    private readonly ReconcileSettings _settings;
    private readonly ILogger<SubscriptionMeterReconciler> _logger;

    public SubscriptionMeterReconciler(IServiceScopeFactory scopes, IOptions<ReconcileSettings> settings,
        ILogger<SubscriptionMeterReconciler> logger)
        => (_scopes, _settings, _logger) = (scopes, settings.Value, logger);

    protected override async Task ExecuteAsync(CancellationToken ct)
    {
        await Task.Delay(TimeSpan.FromSeconds(30), ct);
        var interval = TimeSpan.FromSeconds(_settings.ScanIntervalSeconds > 0 ? _settings.ScanIntervalSeconds : 120);
        while (!ct.IsCancellationRequested)
        {
            try { await ScanOnceAsync(ct); }
            catch (Exception ex) { _logger.LogError(ex, "Meter reconcile failed"); }
            await Task.Delay(interval, ct);
        }
    }

    private async Task ScanOnceAsync(CancellationToken ct)
    {
        if (!_settings.Enabled) return;
        using var scope = _scopes.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<PaymentDbContext>();
        var meters = await db.SubscriptionMeters.AsNoTracking().ToListAsync(ct);
        foreach (var meter in meters)
        {
            var reserved = await db.CreditReservations.CountAsync(r =>
                r.FundedBy == ReservationFunding.SubscriptionMetered &&
                r.Status == ReservationStatus.Reserved &&
                r.MeteredSubscriptionId == meter.SubscriptionId && r.MeteredPeriodStart == meter.PeriodStart, ct);
            if (reserved == meter.ReservedCount) continue;
            var changed = await db.SubscriptionMeters.Where(m => m.SubscriptionId == meter.SubscriptionId &&
                m.PeriodStart == meter.PeriodStart && m.ReservedCount == meter.ReservedCount)
                .ExecuteUpdateAsync(s => s.SetProperty(m => m.ReservedCount, reserved)
                    .SetProperty(m => m.UpdatedAt, _ => DateTime.UtcNow), ct);
            if (changed > 0) _logger.LogWarning("Reconciled meter {SubscriptionId}/{PeriodStart}: {Old} -> {New}",
                meter.SubscriptionId, meter.PeriodStart, meter.ReservedCount, reserved);
        }
    }
}
