using Isas.CampaignService.Models;
using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;

namespace Isas.CampaignService.Tests;

/// <summary>
/// CampaignDbContext chạy trên SQLite in-memory (giữ connection mở để DB sống).
/// Seed entity với Id/CreatedAt set SẴN — tránh default Postgres (gen_random_uuid/now) không có trên SQLite.
/// </summary>
public sealed class CampaignTestDb : IDisposable
{
    private readonly SqliteConnection _conn;
    public CampaignDbContext Db { get; }

    // Connection dùng chung để BackgroundService (StuckScreeningRepublisher) tạo scope DbContext riêng.
    public SqliteConnection Connection => _conn;

    public CampaignTestDb()
    {
        _conn = new SqliteConnection("DataSource=:memory:");
        _conn.Open();
        Db = NewContext();
        Db.Database.EnsureCreated();
    }

    public CampaignDbContext NewContext()
    {
        var options = new DbContextOptionsBuilder<CampaignDbContext>()
            .UseSqlite(_conn)
            // DB2b — UseSnakeCaseNamingConvention() để cột SQLite mang tên snake_case, khớp partial index
            // model-level `HasFilter("published_at IS NULL")` trên outbox_messages (raw SQL cột snake_case).
            // Không bật → EnsureCreated sinh CREATE INDEX tham chiếu cột không tồn tại → vỡ toàn bộ test
            // (precedent DB19 Interview với CHECK constraint). Prod đã snake_case (Program.cs).
            .UseSnakeCaseNamingConvention()
            .Options;
        return new CampaignDbContext(options);
    }

    public void Dispose()
    {
        Db.Dispose();
        _conn.Dispose();
    }

    public static Campaign NewCampaign(
        Guid orgId, CampaignStatus status = CampaignStatus.Draft, bool antiCheat = true)
        => new()
        {
            Id = Guid.NewGuid(),
            OrgId = orgId,
            Title = "Test Campaign",
            Status = status,
            AntiCheatEnabled = antiCheat,
            StartsAt = DateTime.UtcNow,
            CreatedAt = DateTime.UtcNow,
            UpdatedAt = DateTime.UtcNow
        };
}
