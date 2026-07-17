using Isas.AuthService.Models;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata;

namespace Isas.AuthService.Tests;

/// <summary>
/// DB10 — optimistic concurrency (xmin) trên organizations + org_members. Cấu hình gate sau
/// <c>Database.IsNpgsql()</c> nên property `xmin` CHỈ hiện dưới provider Postgres. Dựng model offline
/// dưới Npgsql (không cần DB thật) để soi metadata (IsConcurrencyToken + ValueGenerated.OnAddOrUpdate);
/// đồng thời khẳng định provider SQLite (test EnsureCreated) KHÔNG map `xmin` → gate hoạt động đúng,
/// không phá bộ test SQLite (system column `xmin` không tồn tại trên SQLite).
/// </summary>
public class XminConcurrencyTests
{
    private static AuthDbContext NpgsqlModel()
        => new(new DbContextOptionsBuilder<AuthDbContext>()
            .UseNpgsql("Host=localhost;Database=x").Options);

    [Theory]
    [InlineData(typeof(Organization))]
    [InlineData(typeof(OrgMember))]
    public void Entity_UnderNpgsql_HasXminConcurrencyToken(Type entity)
    {
        using var ctx = NpgsqlModel();
        var p = ctx.Model.FindEntityType(entity)!.FindProperty("xmin");

        Assert.NotNull(p);
        Assert.True(p!.IsConcurrencyToken);
        Assert.Equal(ValueGenerated.OnAddOrUpdate, p.ValueGenerated);
    }

    [Theory]
    [InlineData(typeof(Organization))]
    [InlineData(typeof(OrgMember))]
    public void Entity_UnderSqlite_HasNoXmin(Type entity)
    {
        using var t = new AuthTestDb();
        // Gate IsNpgsql → dưới SQLite không map xmin (nếu map, EnsureCreated cố tạo cột "xmin" → vỡ).
        Assert.Null(t.Db.Model.FindEntityType(entity)!.FindProperty("xmin"));
    }
}
