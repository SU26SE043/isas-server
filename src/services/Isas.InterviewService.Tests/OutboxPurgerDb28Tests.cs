using System.Reflection;
using Isas.InterviewService.ApplicationDbContext;
using Isas.InterviewService.Entities;
using Isas.InterviewService.Models;
using Isas.InterviewService.Services;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;

namespace Isas.InterviewService.Tests;

// DB28 — retention outbox_messages. ĐÂY LÀ ĐƯỜNG XOÁ DỮ LIỆU: test phải khoá chặt CẢ hai chiều —
// xoá đúng thứ đáng xoá, và TUYỆT ĐỐI không đụng row chưa publish (event chưa tới Payment/Campaign).
public class OutboxPurgerDb28Tests
{
    private static async Task PurgeOnce(OutboxPurger p)
    {
        var mi = typeof(OutboxPurger)
            .GetMethod("PurgeOnceAsync", BindingFlags.NonPublic | BindingFlags.Instance)!;
        await (Task)mi.Invoke(p, new object[] { CancellationToken.None })!;
    }

    private static OutboxPurger Build(
        TestDb t, bool enabled = true, int retentionDays = 30,
        int batchSize = 1000, int maxBatches = 10)
    {
        var services = new ServiceCollection();
        services.AddDbContext<InterviewDbContext>(o => o.UseSqlite(t.Connection).UseSnakeCaseNamingConvention());
        var provider = services.BuildServiceProvider();

        return new OutboxPurger(
            provider.GetRequiredService<IServiceScopeFactory>(),
            Options.Create(new OutboxSettings
            {
                PurgeEnabled = enabled,
                PurgeRetentionDays = retentionDays,
                PurgeBatchSize = batchSize,
                PurgeMaxBatchesPerScan = maxBatches
            }),
            NullLogger<OutboxPurger>.Instance);
    }

    // publishedAt = null ⇒ CHƯA phát (dispatcher còn phải gửi).
    private static OutboxMessage Row(DateTime occurredAt, DateTime? publishedAt)
    {
        var row = OutboxMessage.ForScored(new Isas.InterviewService.DTOs.SessionScoredEvent
        {
            SessionId = Guid.NewGuid(),
            CandidateId = Guid.NewGuid(),
            TotalScore = 50m,
            ScoredAt = occurredAt
        });
        row.OccurredAt = occurredAt;
        row.PublishedAt = publishedAt;
        return row;
    }

    private static async Task<int> CountRows(TestDb t) => await t.NewContext().OutboxMessages.CountAsync();

    [Fact]
    public async Task Db28_DeletesPublishedRowsOlderThanRetention()
    {
        using var t = new TestDb();
        var old = DateTime.UtcNow.AddDays(-90);
        t.Db.AddRange(Row(old, old), Row(old, old), Row(old, old));
        await t.Db.SaveChangesAsync();

        await PurgeOnce(Build(t, retentionDays: 30));

        Assert.Equal(0, await CountRows(t));
    }

    // 🔴 RÀO AN TOÀN QUAN TRỌNG NHẤT: row chưa publish = event chưa tới Payment/Campaign. Cũ mấy cũng
    // KHÔNG được xoá (broker chết dài ngày thì nó vẫn phải còn để gửi lại — nếu không là mất tiền/mất mail).
    [Fact]
    public async Task Db28_NeverDeletesUnpublishedRow_EvenWhenAncient()
    {
        using var t = new TestDb();
        var ancient = DateTime.UtcNow.AddYears(-2);
        t.Db.Add(Row(ancient, publishedAt: null));
        await t.Db.SaveChangesAsync();

        await PurgeOnce(Build(t, retentionDays: 1));

        Assert.Equal(1, await CountRows(t));
    }

    // Row đã publish nhưng còn trong hạn giữ → giữ nguyên (cửa sổ đối soát sự cố).
    [Fact]
    public async Task Db28_KeepsRecentlyPublishedRow()
    {
        using var t = new TestDb();
        var recent = DateTime.UtcNow.AddDays(-3);
        t.Db.Add(Row(recent, recent));
        await t.Db.SaveChangesAsync();

        await PurgeOnce(Build(t, retentionDays: 30));

        Assert.Equal(1, await CountRows(t));
    }

    [Fact]
    public async Task Db28_Disabled_DeletesNothing()
    {
        using var t = new TestDb();
        var old = DateTime.UtcNow.AddDays(-90);
        t.Db.Add(Row(old, old));
        await t.Db.SaveChangesAsync();

        await PurgeOnce(Build(t, enabled: false, retentionDays: 30));

        Assert.Equal(1, await CountRows(t));
    }

    // Retention ≤ 0 = TẮT, KHÔNG được diễn giải thành "mọi row đều quá hạn → xoá sạch".
    [Fact]
    public async Task Db28_NonPositiveRetention_IsOff_NotPurgeEverything()
    {
        using var t = new TestDb();
        var old = DateTime.UtcNow.AddDays(-90);
        t.Db.AddRange(Row(old, old), Row(old, old));
        await t.Db.SaveChangesAsync();

        await PurgeOnce(Build(t, retentionDays: 0));

        Assert.Equal(2, await CountRows(t));
    }

    // Trần tuyệt đối mỗi vòng = PurgeBatchSize × PurgeMaxBatchesPerScan; phần dư để vòng sau.
    [Fact]
    public async Task Db28_RespectsPerScanCap_LeavesRemainderForNextScan()
    {
        using var t = new TestDb();
        var old = DateTime.UtcNow.AddDays(-90);
        for (var i = 0; i < 7; i++) t.Db.Add(Row(old.AddMinutes(i), old.AddMinutes(i)));
        await t.Db.SaveChangesAsync();

        await PurgeOnce(Build(t, retentionDays: 30, batchSize: 2, maxBatches: 2));

        Assert.Equal(3, await CountRows(t));   // 7 − (2×2)
    }

    // Purge chạy chung DB với dispatcher: chỉ được ăn phần đã phát, phần chưa phát nguyên vẹn.
    [Fact]
    public async Task Db28_MixedTable_OnlyOldPublishedRemoved()
    {
        using var t = new TestDb();
        var old = DateTime.UtcNow.AddDays(-90);
        var recent = DateTime.UtcNow.AddDays(-1);
        var keepUnpublished = Row(old, publishedAt: null);
        var keepRecent = Row(recent, recent);
        t.Db.AddRange(Row(old, old), keepUnpublished, keepRecent, Row(old, old));
        await t.Db.SaveChangesAsync();

        await PurgeOnce(Build(t, retentionDays: 30));

        var left = await t.NewContext().OutboxMessages.AsNoTracking().Select(m => m.Id).ToListAsync();
        Assert.Equal(2, left.Count);
        Assert.Contains(keepUnpublished.Id, left);
        Assert.Contains(keepRecent.Id, left);
    }
}
