using Isas.AuthService.Models;
using Microsoft.Extensions.Options;

namespace Isas.AuthService.Services;

public sealed class LoginEventPurger(IServiceScopeFactory scopes, IOptions<LoginEventRetentionSettings> options, ILogger<LoginEventPurger> logger) : BackgroundService
{
    protected override async Task ExecuteAsync(CancellationToken ct)
    {
        var settings = options.Value;
        if (!settings.Enabled) return;
        await Task.Delay(TimeSpan.FromSeconds(60), ct);
        var interval = TimeSpan.FromMinutes(settings.ScanIntervalMinutes > 0 ? settings.ScanIntervalMinutes : 60);
        while (!ct.IsCancellationRequested)
        {
            try
            {
                using var scope = scopes.CreateScope();
                var db = scope.ServiceProvider.GetRequiredService<AuthDbContext>();
                var count = await LoginEventPurge.PurgeAsync(db, DateTime.UtcNow, settings, ct);
                if (count > 0) logger.LogInformation("FR18: purged {Count} login_events", count);
            }
            catch (Exception ex) { logger.LogError(ex, "FR18: login-event retention scan failed"); }
            await Task.Delay(interval, ct);
        }
    }
}
