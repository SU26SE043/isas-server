using Isas.Shared.Analytics;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;
using PaymentService.Models;

namespace Isas.PaymentService.Controllers;

[ApiController, Route("admin/traffic"), Authorize(Roles = "Admin")]
public sealed class AdminTrafficController(PaymentDbContext db) : ControllerBase
{
    private static readonly IReadOnlyDictionary<string, AnalyticsGranularity> Allowed = new Dictionary<string, AnalyticsGranularity> { ["hour"] = AnalyticsGranularity.Hour, ["day"] = AnalyticsGranularity.Day };
    [HttpGet]
    public async Task<IActionResult> Get(DateTime? from = null, DateTime? to = null, string? groupBy = null, CancellationToken ct = default)
    {
        if (!AnalyticsPeriod.TryResolve(from, to, groupBy, Allowed, out var period, out var error)) return BadRequest(new { message = error == AnalyticsPeriodError.InvalidRange ? "`from` phải nhỏ hơn `to`." : "`groupBy` chỉ nhận `hour` hoặc `day`." });
        var p = period!;
        var rows = await db.HttpTrafficStats.AsNoTracking().Where(x => x.WindowStart >= p.FromUtc && x.WindowStart < p.ToUtc).ToListAsync(ct);
        object Summary(IEnumerable<HttpTrafficStat> x) { var r = x.Sum(v => (long)v.Requests); var duration = x.Sum(v => v.SumDurationMs); return new { requests = r, errors4xx = x.Where(v => v.StatusClass == "4xx").Sum(v => (long)v.Requests), errors5xx = x.Where(v => v.StatusClass == "5xx").Sum(v => (long)v.Requests), avgDurationMs = r == 0 ? (double?)null : (double)duration / r, maxDurationMs = x.Select(v => (int?)v.MaxDurationMs).Max() }; }
        return Ok(new { from = p.FromUtc, to = p.ToUtc, granularity = p.Granularity.ToString().ToLowerInvariant(), totals = Summary(rows), byRoute = rows.GroupBy(x => x.RouteId).Select(g => new { routeId = g.Key, summary = Summary(g) }), buckets = rows.GroupBy(x => AnalyticsPeriod.BucketKey(x.WindowStart, p.Granularity)).OrderBy(g => g.Key).Select(g => new { periodStart = AnalyticsPeriod.BucketStart(g.Key, p.Granularity), summary = Summary(g) }) });
    }
}
