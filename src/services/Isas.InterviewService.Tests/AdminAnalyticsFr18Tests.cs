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
        Assert.Contains("b2c", System.Text.Json.JsonSerializer.Serialize(ok.Value));
    }
}
