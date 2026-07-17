using System.Reflection;
using Isas.CampaignService.Models;
using Isas.CampaignService.Services;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using Moq;

namespace Isas.CampaignService.Tests;

// DB2b — OutboxDispatcher quét outbox_messages (published_at IS NULL) → publish invitation-email → set
// published_at. At-least-once: publish lỗi (broker down) → giữ null + Attempts++ → vòng sau gửi lại (mail
// không mất). Consumer idempotent theo email_sent_at → phát lại KHÔNG gửi trùng.
public class OutboxDispatcherTests
{
    private static async Task ScanOnce(OutboxDispatcher d)
    {
        var mi = typeof(OutboxDispatcher)
            .GetMethod("ScanOnceAsync", BindingFlags.NonPublic | BindingFlags.Instance)!;
        await (Task)mi.Invoke(d, new object[] { CancellationToken.None })!;
    }

    // ServiceProvider thật để CreateScope() trả CampaignDbContext dùng chung connection SQLite (snake_case
    // khớp schema TestDb — partial index outbox_messages).
    private static OutboxDispatcher Build(CampaignTestDb t, IInvitationEmailPublisher publisher, bool enabled = true)
    {
        var services = new ServiceCollection();
        services.AddDbContext<CampaignDbContext>(o => o.UseSqlite(t.Connection).UseSnakeCaseNamingConvention());
        var provider = services.BuildServiceProvider();

        return new OutboxDispatcher(
            provider.GetRequiredService<IServiceScopeFactory>(),
            publisher,
            Options.Create(new OutboxSettings { Enabled = enabled, ScanIntervalSeconds = 1, BatchSize = 100 }),
            NullLogger<OutboxDispatcher>.Instance);
    }

    private static OutboxMessage Seed(CampaignTestDb t, string payload = "{}")
    {
        var row = new OutboxMessage
        {
            Id = Guid.NewGuid(),
            Type = OutboxMessage.InvitationEmailType,
            Payload = payload,
            InvitationId = Guid.NewGuid(),
            CampaignId = Guid.NewGuid(),
            OccurredAt = DateTime.UtcNow
        };
        t.Db.OutboxMessages.Add(row);
        t.Db.SaveChanges();
        return row;
    }

    [Fact]
    public async Task Scan_UnpublishedRow_PublishedAndMarked()
    {
        using var t = new CampaignTestDb();
        var row = Seed(t, "{\"InvitationId\":\"x\"}");

        string? gotPayload = null, gotMsgId = null;
        var pub = new Mock<IInvitationEmailPublisher>();
        pub.Setup(p => p.PublishRawAsync(It.IsAny<string>(), It.IsAny<string>(), It.IsAny<CancellationToken>()))
           .Callback<string, string, CancellationToken>((p2, id, _) => { gotPayload = p2; gotMsgId = id; })
           .Returns(Task.CompletedTask);

        await ScanOnce(Build(t, pub.Object));

        // Publish gọi với payload NGUYÊN + messageId = row.Id.
        Assert.Equal("{\"InvitationId\":\"x\"}", gotPayload);
        Assert.Equal(row.Id.ToString(), gotMsgId);

        var saved = await t.NewContext().OutboxMessages.AsNoTracking().FirstAsync(m => m.Id == row.Id);
        Assert.NotNull(saved.PublishedAt);   // đã đánh dấu phát
    }

    [Fact]
    public async Task Scan_BrokerDown_KeepsUnpublished_IncrementsAttempts_ThenRetrySucceeds()
    {
        using var t = new CampaignTestDb();
        var row = Seed(t);

        // Lần đầu: broker down → throw.
        var pub = new Mock<IInvitationEmailPublisher>();
        pub.Setup(p => p.PublishRawAsync(It.IsAny<string>(), It.IsAny<string>(), It.IsAny<CancellationToken>()))
           .ThrowsAsync(new Exception("broker down"));

        await ScanOnce(Build(t, pub.Object));   // KHÔNG ném ra ngoài (best-effort mỗi row)

        var afterFail = await t.NewContext().OutboxMessages.AsNoTracking().FirstAsync(m => m.Id == row.Id);
        Assert.Null(afterFail.PublishedAt);     // mail KHÔNG mất — vẫn chờ gửi lại
        Assert.Equal(1, afterFail.Attempts);

        // Vòng sau: broker phục hồi → gửi được.
        var pub2 = new Mock<IInvitationEmailPublisher>();
        pub2.Setup(p => p.PublishRawAsync(It.IsAny<string>(), It.IsAny<string>(), It.IsAny<CancellationToken>()))
            .Returns(Task.CompletedTask);

        await ScanOnce(Build(t, pub2.Object));

        var afterOk = await t.NewContext().OutboxMessages.AsNoTracking().FirstAsync(m => m.Id == row.Id);
        Assert.NotNull(afterOk.PublishedAt);    // gửi lại thành công
    }

    [Fact]
    public async Task Scan_AlreadyPublishedRow_NotRepublished()
    {
        using var t = new CampaignTestDb();
        var row = Seed(t);
        await t.Db.OutboxMessages.Where(m => m.Id == row.Id)
            .ExecuteUpdateAsync(u => u.SetProperty(m => m.PublishedAt, DateTime.UtcNow));

        var pub = new Mock<IInvitationEmailPublisher>();
        pub.Setup(p => p.PublishRawAsync(It.IsAny<string>(), It.IsAny<string>(), It.IsAny<CancellationToken>()))
           .Returns(Task.CompletedTask);

        await ScanOnce(Build(t, pub.Object));

        pub.Verify(p => p.PublishRawAsync(It.IsAny<string>(), It.IsAny<string>(), It.IsAny<CancellationToken>()),
            Times.Never);
    }

    [Fact]
    public async Task Scan_Disabled_NoPublish()
    {
        using var t = new CampaignTestDb();
        Seed(t);

        var pub = new Mock<IInvitationEmailPublisher>();
        pub.Setup(p => p.PublishRawAsync(It.IsAny<string>(), It.IsAny<string>(), It.IsAny<CancellationToken>()))
           .Returns(Task.CompletedTask);

        await ScanOnce(Build(t, pub.Object, enabled: false));

        pub.Verify(p => p.PublishRawAsync(It.IsAny<string>(), It.IsAny<string>(), It.IsAny<CancellationToken>()),
            Times.Never);
    }

    [Fact]
    public async Task Scan_NoRows_NoPublish_NoError()
    {
        using var t = new CampaignTestDb();
        var pub = new Mock<IInvitationEmailPublisher>();
        await ScanOnce(Build(t, pub.Object));   // không có row → không ném, không publish
        pub.Verify(p => p.PublishRawAsync(It.IsAny<string>(), It.IsAny<string>(), It.IsAny<CancellationToken>()),
            Times.Never);
    }

    [Fact]
    public async Task Scan_PartialFailure_OnlyFailedRowStaysUnpublished()
    {
        using var t = new CampaignTestDb();
        var ok = Seed(t);
        var bad = Seed(t);

        var pub = new Mock<IInvitationEmailPublisher>();
        pub.Setup(p => p.PublishRawAsync(It.IsAny<string>(), It.IsAny<string>(), It.IsAny<CancellationToken>()))
           .Returns(Task.CompletedTask);
        // Chỉ row `bad` phát lỗi → nó giữ null + Attempts++, row `ok` vẫn được đánh dấu published.
        pub.Setup(p => p.PublishRawAsync(It.IsAny<string>(), bad.Id.ToString(), It.IsAny<CancellationToken>()))
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
        using var t = new CampaignTestDb();
        for (var i = 0; i < 3; i++) Seed(t);

        var pub = new Mock<IInvitationEmailPublisher>();
        pub.Setup(p => p.PublishRawAsync(It.IsAny<string>(), It.IsAny<string>(), It.IsAny<CancellationToken>()))
           .Returns(Task.CompletedTask);

        var provider = new ServiceCollection()
            .AddDbContext<CampaignDbContext>(o => o.UseSqlite(t.Connection).UseSnakeCaseNamingConvention())
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
        using var t = new CampaignTestDb();
        var older = new OutboxMessage { Id = Guid.NewGuid(), Type = OutboxMessage.InvitationEmailType, Payload = "{}", InvitationId = Guid.NewGuid(), CampaignId = Guid.NewGuid(), OccurredAt = DateTime.UtcNow.AddMinutes(-5) };
        var newer = new OutboxMessage { Id = Guid.NewGuid(), Type = OutboxMessage.InvitationEmailType, Payload = "{}", InvitationId = Guid.NewGuid(), CampaignId = Guid.NewGuid(), OccurredAt = DateTime.UtcNow };
        t.Db.OutboxMessages.AddRange(newer, older);
        await t.Db.SaveChangesAsync();

        var order = new List<string>();
        var pub = new Mock<IInvitationEmailPublisher>();
        pub.Setup(p => p.PublishRawAsync(It.IsAny<string>(), It.IsAny<string>(), It.IsAny<CancellationToken>()))
           .Callback<string, string, CancellationToken>((_, id, _) => order.Add(id))
           .Returns(Task.CompletedTask);

        await ScanOnce(Build(t, pub.Object));

        Assert.Equal(new[] { older.Id.ToString(), newer.Id.ToString() }, order);
    }
}
