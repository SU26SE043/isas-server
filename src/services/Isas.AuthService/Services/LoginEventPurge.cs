using Isas.AuthService.Models;
using Microsoft.EntityFrameworkCore;

namespace Isas.AuthService.Services;

public static class LoginEventPurge
{
    public static async Task<int> PurgeAsync(AuthDbContext db, DateTime nowUtc, LoginEventRetentionSettings settings, CancellationToken ct = default)
    {
        var cutoff = nowUtc.AddDays(-settings.EffectiveRetentionDays);
        var deleted = 0;
        for (var batch = 0; batch < settings.EffectiveMaxBatches; batch++)
        {
            var ids = await db.LoginEvents.AsNoTracking().Where(x => x.CreatedAt < cutoff)
                .OrderBy(x => x.CreatedAt).Select(x => x.Id).Take(settings.EffectiveBatchSize).ToListAsync(ct);
            if (ids.Count == 0) break;
            deleted += await db.LoginEvents.Where(x => ids.Contains(x.Id)).ExecuteDeleteAsync(ct);
            if (ids.Count < settings.EffectiveBatchSize) break;
        }
        return deleted;
    }
}
