using System.Reflection;
using Isas.CampaignService.Models;
using Isas.CampaignService.Services;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;

namespace Isas.CampaignService.Tests;

/// <summary>
/// DB28 — retention outbox_messages. Đây là job XOÁ DỮ LIỆU nên test phải khoá từng điều kiện
/// riêng lẻ, không chỉ "chạy không nổ":
///  • row CHƯA publish (published_at NULL) → GIỮ, dù cũ đến mấy (mail chưa gửi, không được mất).
///  • row đã publish nhưng CHƯA quá hạn → GIỮ.
///  • row đã publish + quá hạn → XOÁ.
///  • trần batch mỗi vòng được tôn trọng.
///  • tắt bằng config → không xoá gì.
/// Mỗi điều kiện có ít nhất 1 test sẽ ĐỎ nếu gỡ đúng điều kiện đó khỏi production (mutation-check).
/// </summary>
public class OutboxPurgerDb28Tests
{
    private static async Task<int> PurgeOnce(OutboxPurger p)
    {
        var mi = typeof(OutboxPurger)
            .GetMethod("PurgeOnceAsync", BindingFlags.NonPublic | BindingFlags.Instance)!;
        return await (Task<int>)mi.Invoke(p, new object[] { CancellationToken.None })!;
    }

    private static OutboxPurger Build(CampaignTestDb t, OutboxSettings? settings = null)
    {
        var provider = new ServiceCollection()
            .AddDbContext<CampaignDbContext>(o => o.UseSqlite(t.Connection).UseSnakeCaseNamingConvention())
            .BuildServiceProvider();

        return new OutboxPurger(
            provider.GetRequiredService<IServiceScopeFactory>(),
            Options.Create(settings ?? new OutboxSettings
            {
                PurgeEnabled = true,
                PurgeRetentionDays = 30,
                PurgeBatchSize = 500
            }),
            NullLogger<OutboxPurger>.Instance);
    }

    // publishedAt = null → row chưa gửi được (dispatcher còn phải retry).
    private static OutboxMessage Seed(CampaignTestDb t, DateTime occurredAt, DateTime? publishedAt)
    {
        var row = new OutboxMessage
        {
            Id = Guid.NewGuid(),
            Type = OutboxMessage.InvitationEmailType,
            Payload = "{}",
            InvitationId = Guid.NewGuid(),
            CampaignId = Guid.NewGuid(),
            OccurredAt = occurredAt,
            PublishedAt = publishedAt
        };
        t.Db.OutboxMessages.Add(row);
        t.Db.SaveChanges();
        return row;
    }

    // MUTATION-CHECK #1 — gỡ `m.PublishedAt != null` khỏi PurgeOnceAsync thì test này ĐỎ:
    // row cũ CHƯA publish sẽ bị xoá mất, tức là mất mail chưa gửi.
    [Fact]
    public async Task Purge_KhongXoa_RowChuaPublish_Du_Rat_Cu()
    {
        using var t = new CampaignTestDb();
        var old = DateTime.UtcNow.AddDays(-365);
        var chuaGui = Seed(t, occurredAt: old, publishedAt: null);

        var deleted = await PurgeOnce(Build(t));

        Assert.Equal(0, deleted);
        Assert.True(await t.NewContext().OutboxMessages.AnyAsync(m => m.Id == chuaGui.Id));
    }

    // MUTATION-CHECK #2 — gỡ `m.PublishedAt < cutoff` (điều kiện quá hạn) thì test này ĐỎ:
    // row vừa gửi xong sẽ bị xoá ngay.
    [Fact]
    public async Task Purge_KhongXoa_RowDaPublish_NhungChuaQuaHan()
    {
        using var t = new CampaignTestDb();
        var moiGui = Seed(t, DateTime.UtcNow.AddDays(-2), publishedAt: DateTime.UtcNow.AddDays(-2));

        var deleted = await PurgeOnce(Build(t));

        Assert.Equal(0, deleted);
        Assert.True(await t.NewContext().OutboxMessages.AnyAsync(m => m.Id == moiGui.Id));
    }

    // Đường chính: rác đã gửi + quá hạn → xoá. Test này ĐỎ nếu purge không thực sự chạy.
    [Fact]
    public async Task Purge_Xoa_RowDaPublish_QuaHan()
    {
        using var t = new CampaignTestDb();
        var rac = Seed(t, DateTime.UtcNow.AddDays(-90), publishedAt: DateTime.UtcNow.AddDays(-90));
        var giu = Seed(t, DateTime.UtcNow.AddDays(-1), publishedAt: DateTime.UtcNow.AddDays(-1));

        var deleted = await PurgeOnce(Build(t));

        Assert.Equal(1, deleted);
        using var db = t.NewContext();
        Assert.False(await db.OutboxMessages.AnyAsync(m => m.Id == rac.Id));
        Assert.True(await db.OutboxMessages.AnyAsync(m => m.Id == giu.Id));
    }

    // Ranh giới retention đọc từ config, không hardcode 30.
    [Fact]
    public async Task Purge_TonTrong_RetentionDays_TuConfig()
    {
        using var t = new CampaignTestDb();
        var row = Seed(t, DateTime.UtcNow.AddDays(-10), publishedAt: DateTime.UtcNow.AddDays(-10));

        // Retention 30 ngày → row 10 ngày còn được giữ.
        Assert.Equal(0, await PurgeOnce(Build(t)));

        // Retention 7 ngày → chính row đó thành quá hạn.
        var deleted = await PurgeOnce(Build(t, new OutboxSettings
        {
            PurgeEnabled = true,
            PurgeRetentionDays = 7,
            PurgeBatchSize = 500
        }));

        Assert.Equal(1, deleted);
        Assert.False(await t.NewContext().OutboxMessages.AnyAsync(m => m.Id == row.Id));
    }

    // MUTATION-CHECK #3 — gỡ `.Take(batch)` thì test này ĐỎ (xoá cả 5 thay vì 2).
    [Fact]
    public async Task Purge_TonTrong_TranBatch_MoiVong()
    {
        using var t = new CampaignTestDb();
        for (var i = 0; i < 5; i++)
            Seed(t, DateTime.UtcNow.AddDays(-90), publishedAt: DateTime.UtcNow.AddDays(-90 + i));

        var purger = Build(t, new OutboxSettings
        {
            PurgeEnabled = true,
            PurgeRetentionDays = 30,
            PurgeBatchSize = 2
        });

        Assert.Equal(2, await PurgeOnce(purger));
        Assert.Equal(3, await t.NewContext().OutboxMessages.CountAsync());

        // Vòng sau dọn tiếp → cuối cùng sạch hết rác quá hạn.
        Assert.Equal(2, await PurgeOnce(purger));
        Assert.Equal(1, await PurgeOnce(purger));
        Assert.Equal(0, await t.NewContext().OutboxMessages.CountAsync());
    }

    // MUTATION-CHECK #4 — gỡ guard `if (!_options.PurgeEnabled) return 0;` thì test này ĐỎ.
    [Fact]
    public async Task Purge_TatBangConfig_KhongXoaGi()
    {
        using var t = new CampaignTestDb();
        Seed(t, DateTime.UtcNow.AddDays(-90), publishedAt: DateTime.UtcNow.AddDays(-90));

        var deleted = await PurgeOnce(Build(t, new OutboxSettings
        {
            PurgeEnabled = false,
            PurgeRetentionDays = 30,
            PurgeBatchSize = 500
        }));

        Assert.Equal(0, deleted);
        Assert.Equal(1, await t.NewContext().OutboxMessages.CountAsync());
    }

    [Fact]
    public async Task Purge_BangRong_KhongLoi()
    {
        using var t = new CampaignTestDb();
        Assert.Equal(0, await PurgeOnce(Build(t)));
    }

    // Purge KHÔNG được cướp việc của dispatcher: row chưa publish sống sót qua purge thì vẫn phải
    // gửi được ở vòng dispatcher kế tiếp (retention không làm mất mail đang chờ).
    [Fact]
    public async Task Purge_KhongPhaDuongRetry_CuaDispatcher()
    {
        using var t = new CampaignTestDb();
        var chuaGui = Seed(t, DateTime.UtcNow.AddDays(-365), publishedAt: null);

        await PurgeOnce(Build(t));

        var conNguyen = await t.NewContext().OutboxMessages.AsNoTracking().FirstAsync(m => m.Id == chuaGui.Id);
        Assert.Null(conNguyen.PublishedAt);       // vẫn ở hàng đợi dispatcher
        Assert.Equal("{}", conNguyen.Payload);    // payload nguyên vẹn để publish lại
    }
}
