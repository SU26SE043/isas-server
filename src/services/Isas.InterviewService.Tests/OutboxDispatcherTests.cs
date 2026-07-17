using System.Reflection;
using Isas.InterviewService.ApplicationDbContext;
using Isas.InterviewService.Entities;
using Isas.InterviewService.Models;
using Isas.InterviewService.Services;
using Isas.InterviewService.Services.Interfaces;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using Moq;

namespace Isas.InterviewService.Tests;

// DB2 — OutboxDispatcher quét outbox_messages (published_at IS NULL) → publish → set published_at.
// At-least-once: publish lỗi (broker down) → giữ null + Attempts++ → vòng sau gửi lại (event không mất).
public class OutboxDispatcherTests
{
    private static async Task ScanOnce(OutboxDispatcher d)
    {
        var mi = typeof(OutboxDispatcher)
            .GetMethod("ScanOnceAsync", BindingFlags.NonPublic | BindingFlags.Instance)!;
        await (Task)mi.Invoke(d, new object[] { CancellationToken.None })!;
    }

    private static OutboxDispatcher Build(TestDb t, ISessionEventPublisher publisher, bool enabled = true)
    {
        var services = new ServiceCollection();
        services.AddDbContext<InterviewDbContext>(o => o.UseSqlite(t.Connection).UseSnakeCaseNamingConvention());
        var provider = services.BuildServiceProvider();

        return new OutboxDispatcher(
            provider.GetRequiredService<IServiceScopeFactory>(),
            publisher,
            Options.Create(new OutboxSettings { Enabled = enabled, ScanIntervalSeconds = 1, BatchSize = 100 }),
            NullLogger<OutboxDispatcher>.Instance);
    }

    private static OutboxMessage Seed(TestDb t, string type = "session.scored", string payload = "{}")
    {
        var row = new OutboxMessage
        {
            Id = Guid.NewGuid(),
            Type = type,
            Payload = payload,
            SessionId = Guid.NewGuid(),
            OccurredAt = DateTime.UtcNow
        };
        t.Db.OutboxMessages.Add(row);
        t.Db.SaveChanges();
        return row;
    }

    [Fact]
    public async Task Scan_UnpublishedRow_PublishedAndMarked()
    {
        using var t = new TestDb();
        var row = Seed(t, "session.scored", "{\"SessionId\":\"x\"}");

        string? gotKey = null, gotPayload = null, gotMsgId = null;
        var pub = new Mock<ISessionEventPublisher>();
        pub.Setup(p => p.PublishRawAsync(It.IsAny<string>(), It.IsAny<string>(), It.IsAny<string>(), It.IsAny<CancellationToken>()))
           .Callback<string, string, string, CancellationToken>((k, p2, id, _) => { gotKey = k; gotPayload = p2; gotMsgId = id; })
           .Returns(Task.CompletedTask);

        await ScanOnce(Build(t, pub.Object));

        // Publish gọi đúng routing key = Type, payload nguyên, messageId = row.Id.
        Assert.Equal("session.scored", gotKey);
        Assert.Equal("{\"SessionId\":\"x\"}", gotPayload);
        Assert.Equal(row.Id.ToString(), gotMsgId);

        var saved = await t.NewContext().OutboxMessages.AsNoTracking().FirstAsync(m => m.Id == row.Id);
        Assert.NotNull(saved.PublishedAt);   // đã đánh dấu phát
    }

    [Fact]
    public async Task Scan_BrokerDown_KeepsUnpublished_IncrementsAttempts_ThenRetrySucceeds()
    {
        using var t = new TestDb();
        var row = Seed(t);

        // Lần đầu: broker down → throw.
        var pub = new Mock<ISessionEventPublisher>();
        pub.Setup(p => p.PublishRawAsync(It.IsAny<string>(), It.IsAny<string>(), It.IsAny<string>(), It.IsAny<CancellationToken>()))
           .ThrowsAsync(new Exception("broker down"));

        await ScanOnce(Build(t, pub.Object));   // KHÔNG ném ra ngoài (best-effort mỗi row)

        var afterFail = await t.NewContext().OutboxMessages.AsNoTracking().FirstAsync(m => m.Id == row.Id);
        Assert.Null(afterFail.PublishedAt);     // event KHÔNG mất — vẫn chờ gửi lại
        Assert.Equal(1, afterFail.Attempts);

        // Vòng sau: broker phục hồi → gửi được.
        var pub2 = new Mock<ISessionEventPublisher>();
        pub2.Setup(p => p.PublishRawAsync(It.IsAny<string>(), It.IsAny<string>(), It.IsAny<string>(), It.IsAny<CancellationToken>()))
            .Returns(Task.CompletedTask);

        await ScanOnce(Build(t, pub2.Object));

        var afterOk = await t.NewContext().OutboxMessages.AsNoTracking().FirstAsync(m => m.Id == row.Id);
        Assert.NotNull(afterOk.PublishedAt);    // gửi lại thành công
    }

    [Fact]
    public async Task Scan_AlreadyPublishedRow_NotRepublished()
    {
        using var t = new TestDb();
        var row = Seed(t);
        await t.Db.OutboxMessages.Where(m => m.Id == row.Id)
            .ExecuteUpdateAsync(u => u.SetProperty(m => m.PublishedAt, DateTime.UtcNow));

        var pub = new Mock<ISessionEventPublisher>();
        pub.Setup(p => p.PublishRawAsync(It.IsAny<string>(), It.IsAny<string>(), It.IsAny<string>(), It.IsAny<CancellationToken>()))
           .Returns(Task.CompletedTask);

        await ScanOnce(Build(t, pub.Object));

        pub.Verify(p => p.PublishRawAsync(It.IsAny<string>(), It.IsAny<string>(), It.IsAny<string>(), It.IsAny<CancellationToken>()),
            Times.Never);
    }

    [Fact]
    public async Task Scan_Disabled_NoPublish()
    {
        using var t = new TestDb();
        Seed(t);

        var pub = new Mock<ISessionEventPublisher>();
        pub.Setup(p => p.PublishRawAsync(It.IsAny<string>(), It.IsAny<string>(), It.IsAny<string>(), It.IsAny<CancellationToken>()))
           .Returns(Task.CompletedTask);

        await ScanOnce(Build(t, pub.Object, enabled: false));

        pub.Verify(p => p.PublishRawAsync(It.IsAny<string>(), It.IsAny<string>(), It.IsAny<string>(), It.IsAny<CancellationToken>()),
            Times.Never);
    }

    [Fact]
    public async Task Scan_NoRows_NoPublish_NoError()
    {
        using var t = new TestDb();
        var pub = new Mock<ISessionEventPublisher>();
        await ScanOnce(Build(t, pub.Object));   // không có row → không ném, không publish
        pub.Verify(p => p.PublishRawAsync(It.IsAny<string>(), It.IsAny<string>(), It.IsAny<string>(), It.IsAny<CancellationToken>()),
            Times.Never);
    }

    [Fact]
    public async Task Scan_PartialFailure_OnlyFailedRowStaysUnpublished()
    {
        using var t = new TestDb();
        var ok = Seed(t);
        var bad = Seed(t);

        var pub = new Mock<ISessionEventPublisher>();
        pub.Setup(p => p.PublishRawAsync(It.IsAny<string>(), It.IsAny<string>(), It.IsAny<string>(), It.IsAny<CancellationToken>()))
           .Returns(Task.CompletedTask);
        // Chỉ row `bad` phát lỗi → nó giữ null + Attempts++, row `ok` vẫn được đánh dấu published.
        pub.Setup(p => p.PublishRawAsync(It.IsAny<string>(), It.IsAny<string>(), bad.Id.ToString(), It.IsAny<CancellationToken>()))
           .ThrowsAsync(new Exception("broker chập chờn"));

        await ScanOnce(Build(t, pub.Object));

        using var db = t.NewContext();
        Assert.NotNull((await db.OutboxMessages.AsNoTracking().FirstAsync(m => m.Id == ok.Id)).PublishedAt);
        var badAfter = await db.OutboxMessages.AsNoTracking().FirstAsync(m => m.Id == bad.Id);
        Assert.Null(badAfter.PublishedAt);
        Assert.Equal(1, badAfter.Attempts);
    }

    [Fact]
    public async Task Scan_RespectsBatchSize()
    {
        using var t = new TestDb();
        for (var i = 0; i < 3; i++) Seed(t);

        var pub = new Mock<ISessionEventPublisher>();
        pub.Setup(p => p.PublishRawAsync(It.IsAny<string>(), It.IsAny<string>(), It.IsAny<string>(), It.IsAny<CancellationToken>()))
           .Returns(Task.CompletedTask);

        var provider = new ServiceCollection()
            .AddDbContext<InterviewDbContext>(o => o.UseSqlite(t.Connection).UseSnakeCaseNamingConvention())
            .BuildServiceProvider();
        var dispatcher = new OutboxDispatcher(
            provider.GetRequiredService<IServiceScopeFactory>(), pub.Object,
            Options.Create(new OutboxSettings { Enabled = true, BatchSize = 2 }),
            NullLogger<OutboxDispatcher>.Instance);

        await ScanOnce(dispatcher);

        // 1 vòng chỉ phát tối đa BatchSize=2 → còn 1 row chưa gửi.
        Assert.Equal(1, await t.NewContext().OutboxMessages.CountAsync(m => m.PublishedAt == null));
    }

    [Fact]
    public async Task Scan_PublishesInOccurredOrder()
    {
        using var t = new TestDb();
        var older = new OutboxMessage { Id = Guid.NewGuid(), Type = "session.scored", Payload = "{}", SessionId = Guid.NewGuid(), OccurredAt = DateTime.UtcNow.AddMinutes(-5) };
        var newer = new OutboxMessage { Id = Guid.NewGuid(), Type = "session.scored", Payload = "{}", SessionId = Guid.NewGuid(), OccurredAt = DateTime.UtcNow };
        t.Db.OutboxMessages.AddRange(newer, older);
        await t.Db.SaveChangesAsync();

        var order = new List<string>();
        var pub = new Mock<ISessionEventPublisher>();
        pub.Setup(p => p.PublishRawAsync(It.IsAny<string>(), It.IsAny<string>(), It.IsAny<string>(), It.IsAny<CancellationToken>()))
           .Callback<string, string, string, CancellationToken>((_, _, id, _) => order.Add(id))
           .Returns(Task.CompletedTask);

        await ScanOnce(Build(t, pub.Object));

        Assert.Equal(new[] { older.Id.ToString(), newer.Id.ToString() }, order);
    }
}
