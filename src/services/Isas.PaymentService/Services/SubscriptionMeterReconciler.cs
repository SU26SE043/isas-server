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
        var now = DateTime.UtcNow;
        var meters = await db.SubscriptionMeters.AsNoTracking()
            .Where(m => db.Subscriptions.Any(s => s.Id == m.SubscriptionId && s.Status == SubscriptionStatus.Active && s.ExpiresAt > now))
            .ToListAsync(ct);
        foreach (var meter in meters)
        {
            try
            {
                var counts = await db.CreditReservations.Where(r => r.FundedBy == ReservationFunding.SubscriptionMetered &&
                    r.MeteredSubscriptionId == meter.SubscriptionId && r.MeteredPeriodStart == meter.PeriodStart)
                    .GroupBy(_ => 1).Select(g => new { Reserved = g.Count(r => r.Status == ReservationStatus.Reserved), Used = g.Count(r => r.Status == ReservationStatus.Consumed) })
                    .FirstOrDefaultAsync(ct);
                var reserved = counts?.Reserved ?? 0; var used = counts?.Used ?? 0;
                if (reserved == meter.ReservedCount && used == meter.UsedCount) continue;
                var changed = await db.SubscriptionMeters.Where(m => m.SubscriptionId == meter.SubscriptionId && m.PeriodStart == meter.PeriodStart && m.ReservedCount == meter.ReservedCount && m.UsedCount == meter.UsedCount)
                    .ExecuteUpdateAsync(s => s.SetProperty(m => m.ReservedCount, reserved).SetProperty(m => m.UsedCount, used).SetProperty(m => m.UpdatedAt, _ => now), ct);
                if (changed > 0) _logger.LogWarning("Reconciled meter {SubscriptionId}/{PeriodStart}: reserved {OldReserved}->{Reserved}, used {OldUsed}->{Used}", meter.SubscriptionId, meter.PeriodStart, meter.ReservedCount, reserved, meter.UsedCount, used);
            }
            catch (Exception ex) { _logger.LogError(ex, "Meter reconcile failed for {SubscriptionId}/{PeriodStart}", meter.SubscriptionId, meter.PeriodStart); }
        }
    }
}
