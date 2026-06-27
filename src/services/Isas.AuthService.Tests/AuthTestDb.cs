using Isas.AuthService.Models;
using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;

namespace Isas.AuthService.Tests;

/// <summary>
/// AuthDbContext chạy trên SQLite in-memory (giữ connection mở để DB sống).
/// Seed entity với Id/CreatedAt set SẴN — tránh default Postgres không có trên SQLite.
/// </summary>
public sealed class AuthTestDb : IDisposable
{
    private readonly SqliteConnection _conn;
    public AuthDbContext Db { get; }

    public AuthTestDb()
    {
        _conn = new SqliteConnection("DataSource=:memory:");
        _conn.Open();
        Db = NewContext();
        Db.Database.EnsureCreated();
    }

    public AuthDbContext NewContext()
    {
        var options = new DbContextOptionsBuilder<AuthDbContext>()
            .UseSqlite(_conn)
            .Options;
        return new AuthDbContext(options);
    }

    public void Dispose()
    {
        Db.Dispose();
        _conn.Dispose();
    }
}
