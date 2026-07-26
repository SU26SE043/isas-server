using Isas.InterviewService.ApplicationDbContext;
using Isas.InterviewService.Enums;
using Isas.Shared.Analytics;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;

namespace Isas.InterviewService.Controllers;

[ApiController]
[Route("api/admin/analytics")]
[Authorize(Roles = "Admin")]
public sealed class AdminAnalyticsController(InterviewDbContext db) : ControllerBase
{
    private static readonly IReadOnlyDictionary<string, AnalyticsGranularity> Allowed =
        new Dictionary<string, AnalyticsGranularity> { ["day"] = AnalyticsGranularity.Day, ["month"] = AnalyticsGranularity.Month };

    [HttpGet]
    public async Task<IActionResult> Get(DateTime? from = null, DateTime? to = null, string? groupBy = null, CancellationToken ct = default)
    {
        if (!AnalyticsPeriod.TryResolve(from, to, groupBy, Allowed, out var period, out var error))
            return BadRequest(new { message = error == AnalyticsPeriodError.InvalidRange ? "`from` phải nhỏ hơn `to`." : "`groupBy` chỉ nhận `day` hoặc `month`." });
        var resolved = period!;
        var active = new[] { SessionStatus.GeneratingQuestions, SessionStatus.Ready, SessionStatus.InProgress, SessionStatus.Scoring };
        var sessions = await db.PracticeSessions.AsNoTracking().ToListAsync(ct);
        var answers = await db.PracticeAnswers.AsNoTracking().ToListAsync(ct);
        var bucketRows = sessions
            .Where(s => s.CreatedAt >= resolved.FromUtc && s.CreatedAt < resolved.ToUtc)
            .Select(s => new { Key = AnalyticsPeriod.BucketKey(s.CreatedAt, resolved.Granularity), Created = 1, Scored = 0, Failed = 0, Abandoned = 0 })
            .Concat(sessions.Where(s => s.CompletedAt != null && s.CompletedAt >= resolved.FromUtc && s.CompletedAt < resolved.ToUtc)
                .Select(s => new { Key = AnalyticsPeriod.BucketKey(s.CompletedAt!.Value, resolved.Granularity), Created = 0, Scored = s.Status == SessionStatus.Scored ? 1 : 0, Failed = s.Status == SessionStatus.Failed ? 1 : 0, Abandoned = s.Status == SessionStatus.SessionAbandoned ? 1 : 0 }))
            .GroupBy(x => x.Key).OrderBy(g => g.Key)
            .Select(g => new { periodStart = AnalyticsPeriod.BucketStart(g.Key, resolved.Granularity), created = g.Sum(x => x.Created), scored = g.Sum(x => x.Scored), failed = g.Sum(x => x.Failed), abandoned = g.Sum(x => x.Abandoned) }).ToList();
        return Ok(new {
            from = resolved.FromUtc, to = resolved.ToUtc, granularity = resolved.Granularity.ToString().ToLowerInvariant(),
            activeSessions = new { b2c = sessions.Count(s => active.Contains(s.Status) && s.CampaignId == null), b2b = sessions.Count(s => active.Contains(s.Status) && s.CampaignId != null) },
            totals = new { answersUploaded = answers.Count(a => a.Status == AnswerStatus.Uploaded), answersNeedsReview = answers.Count(a => a.NeedsReview), byJobCategory = sessions.GroupBy(s => s.JobCategory.ToString()).Select(g => new { jobCategory = g.Key, count = g.Count() }) },
            buckets = bucketRows
        });
    }
}
