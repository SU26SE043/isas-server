using Isas.InterviewService.ApplicationDbContext;
using Isas.InterviewService.Entities;
using Microsoft.EntityFrameworkCore;

namespace Isas.InterviewService.Tests;

/// <summary>
/// DB10 — optimistic concurrency token xmin trên practice_sessions (system column Postgres, KHÔNG DDL).
/// Chỉ áp cho Npgsql (gate provider trong OnModelCreating) → SQLite (EnsureCreated) không dựng.
/// Dựng model OFFLINE bằng UseNpgsql (KHÔNG mở kết nối — chỉ đọc <c>.Model</c>) để soi token.
/// </summary>
public class XminConcurrencyTokenTests
{
    // UseNpgsql với connection string giả: model được build offline (Database.IsNpgsql()==true),
    // không cần server thật vì chỉ truy cập .Model (không query).
    private static InterviewDbContext NpgsqlModelContext()
    {
        var options = new DbContextOptionsBuilder<InterviewDbContext>()
            .UseNpgsql("Host=localhost;Database=offline_model_only")
            .UseSnakeCaseNamingConvention()
            .Options;
        return new InterviewDbContext(options);
    }

    [Fact]
    public void PracticeSession_HasXminConcurrencyToken_OnNpgsql()
    {
        using var ctx = NpgsqlModelContext();
        var entity = ctx.Model.FindEntityType(typeof(PracticeSession))!;

        var token = entity.GetProperties().SingleOrDefault(p =>
            p.IsConcurrencyToken && p.GetColumnName() == "xmin");

        Assert.NotNull(token);
        Assert.Equal("xid", token!.GetColumnType());
        Assert.Equal(typeof(uint), token.ClrType);
        Assert.Equal(Microsoft.EntityFrameworkCore.Metadata.ValueGenerated.OnAddOrUpdate, token.ValueGenerated);
    }

    // Gate provider: SQLite (test EnsureCreated) KHÔNG dựng xmin (SQLite không có system column này).
    [Fact]
    public void PracticeSession_HasNoXminToken_OnSqlite()
    {
        using var t = new TestDb();
        var entity = t.Db.Model.FindEntityType(typeof(PracticeSession))!;

        Assert.DoesNotContain(entity.GetProperties(), p => p.GetColumnName() == "xmin");
    }
}
