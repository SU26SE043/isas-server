using Isas.AuthService.DTOs;
using Isas.AuthService.Services;
using Isas.Shared.Analytics;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;

namespace Isas.AuthService.Controllers;

[ApiController]
[Route("auth/admin/analytics")]
[Authorize(Roles = "Admin")]
public sealed class AdminAnalyticsController(IAuthAnalyticsService analytics) : ControllerBase
{
    private static readonly IReadOnlyDictionary<string, AnalyticsGranularity> Allowed =
        new Dictionary<string, AnalyticsGranularity> { ["day"] = AnalyticsGranularity.Day, ["month"] = AnalyticsGranularity.Month };

    [HttpGet]
    public async Task<ActionResult<AuthAnalyticsResponse>> Get(
        [FromQuery] DateTime? from = null, [FromQuery] DateTime? to = null,
        [FromQuery] string? groupBy = null, CancellationToken ct = default)
    {
        if (!AnalyticsPeriod.TryResolve(from, to, groupBy, Allowed, out var period, out var error))
            return error == AnalyticsPeriodError.InvalidRange
                ? BadRequest(new { message = "`from` phải nhỏ hơn `to`." })
                : BadRequest(new { message = "`groupBy` chỉ nhận `day` hoặc `month`." });
        return Ok(await analytics.GetAsync(period!, ct));
    }
}
