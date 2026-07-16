using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using PaymentService.Models;

namespace Isas.PaymentService.Tests;

/// <summary>
/// PaymentDbContext chạy trên SQLite in-memory (giữ connection mở để DB sống).
/// DB1 — dùng UseSnakeCaseNamingConvention() để cột SQLite mang tên snake_case khớp raw SQL của
/// model-level CHECK (remaining_credits/reserved_credits/period_usage/delta). Không có convention,
/// EnsureCreated sinh CHECK tham chiếu cột snake_case không tồn tại → vỡ toàn bộ Payment test.
/// Test dùng LINQ (property expression) nên đổi tên cột không ảnh hưởng hành vi CRUD.
/// </summary>
public sealed class PaymentTestDb : IDisposable
{
    private readonly SqliteConnection _conn;
    public PaymentDbContext Db { get; }

    public PaymentTestDb()
    {
        _conn = new SqliteConnection("DataSource=:memory:");
        _conn.Open();
        Db = NewContext();
        Db.Database.EnsureCreated();
    }

    public PaymentDbContext NewContext()
    {
        var options = new DbContextOptionsBuilder<PaymentDbContext>()
            .UseSqlite(_conn)
            .UseSnakeCaseNamingConvention()
            .Options;
        return new PaymentDbContext(options);
    }

    public void Dispose()
    {
        Db.Dispose();
        _conn.Dispose();
    }
}
