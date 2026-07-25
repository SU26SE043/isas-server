using Microsoft.EntityFrameworkCore;
using PaymentService.Models;
namespace Isas.PaymentService.Services;
public static class HttpTrafficPurge
{
    public static async Task<int> PurgeAsync(PaymentDbContext db, DateTime nowUtc, int retentionDays = 90, CancellationToken ct = default)
    {
        var cutoff = nowUtc.AddDays(-(retentionDays > 0 ? retentionDays : 90));
        return await db.HttpTrafficStats.Where(x => x.WindowStart < cutoff).ExecuteDeleteAsync(ct);
    }
}
