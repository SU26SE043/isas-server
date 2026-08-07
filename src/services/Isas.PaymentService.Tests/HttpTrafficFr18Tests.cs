using Isas.PaymentService.Controllers;
using Isas.PaymentService.DTOs;
using PaymentService.Models;
using Isas.PaymentService.Services;
using Microsoft.AspNetCore.Mvc;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Logging.Abstractions;

namespace Isas.PaymentService.Tests;
public sealed class HttpTrafficFr18Tests
{
    [Fact]
    public async Task Report_UsesHalfOpenRange_AndWeightedAverage()
    {
        using var t = new PaymentTestDb(); var at = new DateTime(2026, 7, 1, 0, 0, 0, DateTimeKind.Utc);
        t.Db.HttpTrafficStats.AddRange(
            new HttpTrafficStat { Id = Guid.NewGuid(), WindowStart = at, WindowEnd = at.AddMinutes(5), RouteId = "auth-route", StatusClass = "2xx", Requests = 2, SumDurationMs = 100, MaxDurationMs = 70, CreatedAt = at },
            new HttpTrafficStat { Id = Guid.NewGuid(), WindowStart = at, WindowEnd = at.AddMinutes(5), RouteId = "auth-route", StatusClass = "5xx", Requests = 1, SumDurationMs = 200, MaxDurationMs = 200, CreatedAt = at },
            new HttpTrafficStat { Id = Guid.NewGuid(), WindowStart = at.AddDays(1), WindowEnd = at.AddDays(1).AddMinutes(5), RouteId = "edge", StatusClass = "4xx", Requests = 9, SumDurationMs = 9, MaxDurationMs = 1, CreatedAt = at });
        await t.Db.SaveChangesAsync();
        var result = await new AdminTrafficController(t.Db).Get(at, at.AddDays(1), "day");
        var json = System.Text.Json.JsonSerializer.Serialize(Assert.IsType<OkObjectResult>(result).Value);
        Assert.Contains("\"requests\":3", json); Assert.Contains("\"errors5xx\":1", json); Assert.Contains("\"avgDurationMs\":100", json); Assert.DoesNotContain("\"requests\":9", json);
    }
    // Q5 — nhánh LAZY: `buckets` là IEnumerable chưa ToList(), chỉ được duyệt lúc SERIALIZE, tức SAU khi
    // `Ok(...)` đã return ⇒ exception rơi ngoài action. `Assert.IsType<OkObjectResult>` KHÔNG bắt được;
    // phải serialize thật. Test cũ ở trên mù ca này vì dải `[at, at+1d)` chỉ chừa lại ĐÚNG 1 bucket —
    // thêm test mới thay vì nới dải của nó (dải nửa mở đang là guard riêng, nới là làm yếu guard khác).
    [Fact]
    public async Task Report_NhieuBucket_SapTheoThoiGian_KhongNemLucSerialize()
    {
        using var t = new PaymentTestDb(); var at = new DateTime(2026, 7, 1, 0, 0, 0, DateTimeKind.Utc);
        t.Db.HttpTrafficStats.AddRange(
            new HttpTrafficStat { Id = Guid.NewGuid(), WindowStart = at.AddDays(2), WindowEnd = at.AddDays(2).AddMinutes(5), RouteId = "r", StatusClass = "2xx", Requests = 1, SumDurationMs = 10, MaxDurationMs = 10, CreatedAt = at },
            new HttpTrafficStat { Id = Guid.NewGuid(), WindowStart = at, WindowEnd = at.AddMinutes(5), RouteId = "r", StatusClass = "2xx", Requests = 1, SumDurationMs = 10, MaxDurationMs = 10, CreatedAt = at },
            new HttpTrafficStat { Id = Guid.NewGuid(), WindowStart = at.AddDays(1), WindowEnd = at.AddDays(1).AddMinutes(5), RouteId = "r", StatusClass = "2xx", Requests = 1, SumDurationMs = 10, MaxDurationMs = 10, CreatedAt = at });
        await t.Db.SaveChangesAsync();

        var result = await new AdminTrafficController(t.Db).Get(at, at.AddDays(3), "day");
        var json = System.Text.Json.JsonSerializer.Serialize(Assert.IsType<OkObjectResult>(result).Value);

        // Assert THỨ TỰ, không chỉ "không ném" — mutation đảo dấu CompareTo phải đỏ ở đây.
        var mocs = System.Text.RegularExpressions.Regex.Matches(json, "\"periodStart\":\"(?<v>[^\"]+)\"")
            .Select(m => m.Groups["v"].Value).ToList();
        Assert.Equal(3, mocs.Count);
        Assert.Equal(mocs.OrderBy(v => v, StringComparer.Ordinal).ToList(), mocs);
    }

    [Fact]
    public async Task Purge_DeletesOnlyOlderThanNinetyDays()
    {
        using var t = new PaymentTestDb(); var now = DateTime.UtcNow;
        t.Db.HttpTrafficStats.AddRange(new HttpTrafficStat { Id=Guid.NewGuid(), WindowStart=now.AddDays(-91), WindowEnd=now, RouteId="a", StatusClass="2xx", CreatedAt=now }, new HttpTrafficStat { Id=Guid.NewGuid(), WindowStart=now.AddDays(-90), WindowEnd=now, RouteId="b", StatusClass="2xx", CreatedAt=now }); await t.Db.SaveChangesAsync();
        Assert.Equal(1, await HttpTrafficPurge.PurgeAsync(t.Db, now));
    }

    [Fact]
    public async Task InternalSink_RejectsWrongToken()
    {
        using var t = new PaymentTestDb();
        var c = new InternalHttpTrafficController(t.Db, Config(), NullLogger<InternalHttpTrafficController>.Instance);
        var result = await c.Record(new RecordHttpTrafficRequest(DateTime.UtcNow, DateTime.UtcNow, "auth-route", "2xx", 1, 1, 1), "wrong", default);
        Assert.IsType<UnauthorizedObjectResult>(result);
    }

    [Fact]
    public async Task InternalSink_WhenStoreFails_ReturnsAcceptedDropped()
    {
        using var t = new PaymentTestDb();
        var db = t.NewContext();
        db.Dispose();
        var c = new InternalHttpTrafficController(db, Config(), NullLogger<InternalHttpTrafficController>.Instance);
        var result = await c.Record(new RecordHttpTrafficRequest(DateTime.UtcNow, DateTime.UtcNow, "auth-route", "2xx", 1, 1, 1), "test-token", default);
        var accepted = Assert.IsType<AcceptedResult>(result);
        Assert.NotNull(accepted.Value);
    }

    private static IConfiguration Config() => new ConfigurationBuilder().AddInMemoryCollection(new Dictionary<string, string?> { ["Internal:Token"] = "test-token" }).Build();
}
