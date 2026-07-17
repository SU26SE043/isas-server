using Isas.CampaignService.Models;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata;

namespace Isas.CampaignService.Tests;

/// <summary>
/// DB10 — optimistic concurrency trên `campaigns` qua system column Postgres `xmin`.
/// Map là provider-gated (IsNpgsql), nên phải soi model dựng OFFLINE bằng provider Npgsql
/// (không cần kết nối DB — chỉ finalize model). SQLite test không có xmin (gate loại ra).
/// Pattern: SweeperIndexTests (model-inspection), nhưng xmin cần model Npgsql.
/// </summary>
public class CampaignXminConcurrencyDb10Tests
{
    // Dựng model Npgsql offline (không mở kết nối) — chỉ để soi metadata concurrency token.
    private static Microsoft.EntityFrameworkCore.Metadata.IEntityType NpgsqlCampaignEntity()
    {
        var options = new DbContextOptionsBuilder<CampaignDbContext>()
            .UseNpgsql("Host=localhost;Database=none;Username=none;Password=none")
            .UseSnakeCaseNamingConvention()
            .Options;
        using var db = new CampaignDbContext(options);
        return db.Model.FindEntityType(typeof(Campaign))!;
    }

    // Provider Npgsql: campaigns map thuộc tính shadow `xmin` làm concurrency token (cột hệ thống xid).
    [Fact]
    public void Campaigns_MapXminConcurrencyToken_OnNpgsql()
    {
        var entity = NpgsqlCampaignEntity();
        var xmin = entity.FindProperty("xmin");

        Assert.NotNull(xmin);
        Assert.True(xmin!.IsConcurrencyToken);
        Assert.Equal(ValueGenerated.OnAddOrUpdate, xmin.ValueGenerated);
        Assert.Equal("xmin", xmin.GetColumnName());
        Assert.Equal("xid", xmin.GetColumnType());
    }

    // Gate IsNpgsql: SQLite (test) KHÔNG map xmin → EnsureCreated không tham chiếu cột hệ thống Postgres.
    [Fact]
    public void Campaigns_NoXmin_OnSqlite()
    {
        using var tdb = new CampaignTestDb();
        var entity = tdb.Db.Model.FindEntityType(typeof(Campaign))!;

        Assert.Null(entity.FindProperty("xmin"));
    }
}
