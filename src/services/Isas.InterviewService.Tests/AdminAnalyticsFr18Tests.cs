using Isas.InterviewService.Controllers;
using Isas.InterviewService.Enums;
using Microsoft.AspNetCore.Mvc;

namespace Isas.InterviewService.Tests;

public sealed class AdminAnalyticsFr18Tests
{
    [Fact]
    public async Task Analytics_SeparatesActiveB2CAndB2B()
    {
        using var t = new TestDb();
        t.Db.PracticeSessions.AddRange(
            TestDb.Session(Guid.NewGuid(), SessionStatus.Ready),
            TestDb.Session(Guid.NewGuid(), SessionStatus.Scoring, campaignId: Guid.NewGuid()),
            TestDb.Session(Guid.NewGuid(), SessionStatus.Scored, campaignId: Guid.NewGuid()));
        await t.Db.SaveChangesAsync();
        var result = await new AdminAnalyticsController(t.Db).Get();
        var ok = Assert.IsType<OkObjectResult>(result);
        var json = System.Text.Json.JsonSerializer.Serialize(ok.Value);
        Assert.Contains("\"b2c\":1", json);
        Assert.Contains("\"b2b\":1", json);
    }

    [Fact]
    public async Task Analytics_UsesCompletedAtForTerminalBuckets_AndHalfOpenRange()
    {
        using var t = new TestDb();
        var start = new DateTime(2026, 7, 1, 0, 0, 0, DateTimeKind.Utc);
        var scored = TestDb.Session(Guid.NewGuid(), SessionStatus.Scored, createdAt: start.AddDays(-2));
        scored.CompletedAt = start.AddHours(2);
        var boundary = TestDb.Session(Guid.NewGuid(), SessionStatus.Failed, createdAt: start);
        boundary.CompletedAt = start.AddDays(1);
        t.Db.PracticeSessions.AddRange(scored, boundary);
        await t.Db.SaveChangesAsync();

        var result = await new AdminAnalyticsController(t.Db).Get(start, start.AddDays(1), "day");
        var json = System.Text.Json.JsonSerializer.Serialize(Assert.IsType<OkObjectResult>(result).Value);
        Assert.Contains("\"scored\":1", json);
        Assert.DoesNotContain("\"failed\":1", json);
    }
}
