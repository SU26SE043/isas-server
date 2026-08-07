using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Diagnostics;
using Microsoft.EntityFrameworkCore.Storage;
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

    /// <summary>Connection dùng chung (mở suốt đời harness) — cho test cần build ServiceProvider/DbContext
    /// riêng trên cùng DB in-memory (vd DB4 reconciler BackgroundService dùng IServiceScopeFactory).</summary>
    public SqliteConnection Connection => _conn;

    public PaymentTestDb()
    {
        _conn = new SqliteConnection("DataSource=:memory:");
        _conn.Open();
        Db = NewContext();
        Db.Database.EnsureCreated();
    }

    public PaymentDbContext NewContext() => NewContext(null, null);

    /// <summary>
    /// DB25b — biến thể cho phép cắm <paramref name="strategy"/> (execution strategy) và
    /// <paramref name="interceptors"/>. Cả hai mặc định <c>null</c> ⇒ hành vi y hệt
    /// <see cref="NewContext()"/>, nên mọi test cũ không đổi một dòng.
    ///
    /// Cần thiết vì SQLite mặc định chạy chiến lược KHÔNG-retry, tức là ràng buộc "không được tự mở
    /// transaction" của Npgsql <c>EnableRetryOnFailure</c> không bao giờ được kiểm trong CI.
    /// </summary>
    public PaymentDbContext NewContext(
        Func<ExecutionStrategyDependencies, IExecutionStrategy>? strategy,
        IEnumerable<IInterceptor>? interceptors)
    {
        var builder = new DbContextOptionsBuilder<PaymentDbContext>()
            .UseSqlite(_conn, o => { if (strategy is not null) o.ExecutionStrategy(strategy); })
            .UseSnakeCaseNamingConvention();
        if (interceptors is not null) builder.AddInterceptors(interceptors);
        return new PaymentDbContext(builder.Options);
    }

    public void Dispose()
    {
        Db.Dispose();
        _conn.Dispose();
    }
}
