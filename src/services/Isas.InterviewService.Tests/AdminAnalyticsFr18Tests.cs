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

    // Q5 — nhánh EAGER (`.ToList()` ngay trong action, khác Campaign/Payment ném lúc serialize).
    // Hai test trên mù ca này vì fixture của chúng ra ĐÚNG 1 bucket: 1 phần tử thì sort không phải so
    // sánh gì, nên `AnalyticsBucketKey` thiếu IComparable vẫn xanh. Đo trên deploy 2026-08-07: endpoint
    // này 500 với mọi dải ngày thật.
    [Fact]
    public async Task Analytics_NhieuBucket_SapTheoThoiGian()
    {
        using var t = new TestDb();
        var start = new DateTime(2026, 7, 1, 0, 0, 0, DateTimeKind.Utc);
        // Thêm không theo thứ tự để phép sắp có việc thật.
        t.Db.PracticeSessions.AddRange(
            TestDb.Session(Guid.NewGuid(), SessionStatus.Ready, createdAt: start.AddDays(2)),
            TestDb.Session(Guid.NewGuid(), SessionStatus.Ready, createdAt: start),
            TestDb.Session(Guid.NewGuid(), SessionStatus.Ready, createdAt: start.AddDays(1)));
        await t.Db.SaveChangesAsync();

        var result = await new AdminAnalyticsController(t.Db).Get(start, start.AddDays(3), "day");
        var json = System.Text.Json.JsonSerializer.Serialize(Assert.IsType<OkObjectResult>(result).Value);

        var mocs = System.Text.RegularExpressions.Regex.Matches(json, "\"periodStart\":\"(?<v>[^\"]+)\"")
            .Select(m => m.Groups["v"].Value).ToList();
        Assert.Equal(3, mocs.Count);
        Assert.Equal(mocs.OrderBy(v => v, StringComparer.Ordinal).ToList(), mocs);
    }
}
