using Isas.PaymentService.Models;
using Microsoft.Extensions.Options;
using PaymentService.Models;

namespace Isas.PaymentService.Services;
public sealed class HttpTrafficPurger(IServiceScopeFactory scopes, IOptions<HttpTrafficRetentionSettings> options, ILogger<HttpTrafficPurger> logger) : BackgroundService
{
    protected override async Task ExecuteAsync(CancellationToken ct)
    {
        var settings = options.Value;
        if (!settings.Enabled) return;
        await Task.Delay(TimeSpan.FromSeconds(60), ct);
        var interval = TimeSpan.FromMinutes(settings.ScanIntervalMinutes > 0 ? settings.ScanIntervalMinutes : 60);
        while (!ct.IsCancellationRequested)
        {
            try { using var scope = scopes.CreateScope(); var db = scope.ServiceProvider.GetRequiredService<PaymentDbContext>(); var deleted = await HttpTrafficPurge.PurgeAsync(db, DateTime.UtcNow, settings.RetentionDays, ct); if (deleted > 0) logger.LogInformation("FR18: purged {Count} http_traffic_stats", deleted); }
            catch (Exception ex) { logger.LogError(ex, "FR18: traffic retention scan failed"); }
            await Task.Delay(interval, ct);
        }
    }
}
