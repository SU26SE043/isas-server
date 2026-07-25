using Isas.AuthService.DTOs;
using Isas.AuthService.Models;
using Isas.Shared.Analytics;
using Microsoft.EntityFrameworkCore;

namespace Isas.AuthService.Services;

public interface IAuthAnalyticsService
{
    Task<AuthAnalyticsResponse> GetAsync(AnalyticsPeriodResult period, CancellationToken ct = default);
}

public sealed class AuthAnalyticsService(AuthDbContext db) : IAuthAnalyticsService
{
    public async Task<AuthAnalyticsResponse> GetAsync(AnalyticsPeriodResult period, CancellationToken ct = default)
    {
        var now = DateTime.UtcNow;
        var users = await db.Users.AsNoTracking().ToListAsync(ct);
        var events = await db.LoginEvents.AsNoTracking()
            .Where(x => x.CreatedAt >= period.FromUtc && x.CreatedAt < period.ToUtc)
            .ToListAsync(ct);
        var activeEvents = await db.LoginEvents.AsNoTracking()
            .Where(x => x.CreatedAt >= now.AddDays(-30))
            .ToListAsync(ct);
        var roles = await (from ur in db.UserRoles.AsNoTracking()
                           join r in db.Roles.AsNoTracking() on ur.RoleId equals r.Id
                           select new { r.Name }).ToListAsync(ct);

        var buckets = users.Where(x => x.CreatedAt >= period.FromUtc && x.CreatedAt < period.ToUtc)
            .Select(x => (Key: AnalyticsPeriod.BucketKey(x.CreatedAt, period.Granularity), NewUsers: 1, Login: 0, UserId: Guid.Empty))
            .Concat(events.Select(x => (Key: AnalyticsPeriod.BucketKey(x.CreatedAt, period.Granularity), NewUsers: 0, Login: 1, UserId: x.UserId)))
            .GroupBy(x => x.Key)
            .OrderBy(x => x.Key)
            .Select(x => new AuthAnalyticsBucket(
                AnalyticsPeriod.BucketStart(x.Key, period.Granularity),
                x.Sum(v => v.NewUsers), x.Sum(v => v.Login), x.Where(v => v.UserId != Guid.Empty).Select(v => v.UserId).Distinct().Count()))
            .ToList();

        return new AuthAnalyticsResponse
        {
            From = period.FromUtc,
            To = period.ToUtc,
            Granularity = period.Granularity.ToString().ToLowerInvariant(),
            Totals = new AuthAnalyticsTotals
            {
                TotalUsers = users.Count,
                NewUsers = users.Count(x => x.CreatedAt >= period.FromUtc && x.CreatedAt < period.ToUtc),
                BannedUsers = users.Count(x => x.BannedAt is not null),
                TotalOrganizations = await db.Organizations.CountAsync(ct),
                ByRole = roles.Where(x => x.Name is not null).GroupBy(x => x.Name!).OrderBy(x => x.Key)
                    .Select(x => new RoleCount(x.Key, x.Count())).ToList()
            },
            ActiveUsers = new AuthActiveUsers(
                activeEvents.Where(x => x.CreatedAt >= now.AddDays(-7)).Select(x => x.UserId).Distinct().Count(),
                activeEvents.Select(x => x.UserId).Distinct().Count()),
            Buckets = buckets
        };
    }
}
