using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata;
using PaymentService.Models;

namespace Isas.PaymentService.Tests;

/// <summary>
/// DB10 — optimistic concurrency (xmin) trên credit_accounts (ví tiền), defense-in-depth. Cấu hình gate
/// sau <c>Database.IsNpgsql()</c> nên property `xmin` CHỈ hiện dưới provider Postgres. Dựng model offline
/// dưới Npgsql (không cần DB thật) để soi metadata; đồng thời khẳng định provider SQLite (test
/// EnsureCreated) KHÔNG map `xmin` → gate hoạt động đúng, không phá bộ test SQLite (system column `xmin`
/// không tồn tại trên SQLite).
/// </summary>
public class XminConcurrencyTests
{
    private static PaymentDbContext NpgsqlModel()
        => new(new DbContextOptionsBuilder<PaymentDbContext>()
            .UseNpgsql("Host=localhost;Database=x").Options);

    [Fact]
    public void CreditAccount_UnderNpgsql_HasXminConcurrencyToken()
    {
        using var ctx = NpgsqlModel();
        var p = ctx.Model.FindEntityType(typeof(CreditAccount))!.FindProperty("xmin");

        Assert.NotNull(p);
        Assert.True(p!.IsConcurrencyToken);
        Assert.Equal(ValueGenerated.OnAddOrUpdate, p.ValueGenerated);
    }

    [Fact]
    public void CreditAccount_UnderSqlite_HasNoXmin()
    {
        using var t = new PaymentTestDb();
        // Gate IsNpgsql → dưới SQLite không map xmin (nếu map, EnsureCreated cố tạo cột "xmin" → vỡ).
        Assert.Null(t.Db.Model.FindEntityType(typeof(CreditAccount))!.FindProperty("xmin"));
    }
}
