using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using PaymentService.Models;

namespace Isas.PaymentService.Tests;

/// <summary>
/// PaymentDbContext chạy trên SQLite in-memory (giữ connection mở để DB sống).
/// Không dùng UseSnakeCaseNamingConvention() ở test (SQLite provider không cần; convention chỉ
/// đổi tên cột/bảng — không ảnh hưởng hành vi CRUD được test ở đây).
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
            .Options;
        return new PaymentDbContext(options);
    }

    public void Dispose()
    {
        Db.Dispose();
        _conn.Dispose();
    }
}
