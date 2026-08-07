using System.Data.Common;
using Microsoft.EntityFrameworkCore.Diagnostics;
using Microsoft.EntityFrameworkCore.Storage;

namespace Isas.PaymentService.Tests;

/// <summary>Lỗi "tạm thời" giả — chỉ dùng để ép execution strategy chạy lại delegate.</summary>
public sealed class TransientTestException(string message) : Exception(message);

/// <summary>
/// Chiến lược thử lại THẬT (tối đa 3 lần, không chờ) cho đúng <see cref="TransientTestException"/>.
/// Npgsql thật thử lại khi mất kết nối/deadlock; ở đây ta dựng lại đúng hình dạng đó trên SQLite.
/// </summary>
public sealed class RetryOnTestFaultStrategy(ExecutionStrategyDependencies dependencies)
    : ExecutionStrategy(dependencies, maxRetryCount: 3, maxRetryDelay: TimeSpan.Zero)
{
    public override bool RetriesOnFailure => true;

    // EF bọc lỗi của interceptor vào DbUpdateException → phải dò cả chuỗi InnerException.
    protected override bool ShouldRetryOn(Exception exception)
    {
        for (var e = exception; e is not null; e = e.InnerException)
            if (e is TransientTestException) return true;
        return false;
    }
}

/// <summary>
/// Ném <see cref="TransientTestException"/> đúng MỘT lần, tại câu <c>INSERT</c> chạm bảng
/// <paramref name="table"/>. Mục đích: để lần thử đầu hỏng SAU khi các câu lệnh trước đó trong
/// transaction đã chạy — đúng khoảnh khắc mà change tracker còn giữ entity ở trạng thái Added.
/// </summary>
public sealed class ThrowOnceInterceptor(string table) : DbCommandInterceptor
{
    private int _fired;

    /// <summary>Đã kích hoạt chưa — test phải assert cái này, nếu không phép thử là rỗng.</summary>
    public bool Fired => Volatile.Read(ref _fired) > 0;

    private void MaybeThrow(DbCommand command)
    {
        var sql = command.CommandText;
        if (sql.Contains(table, StringComparison.OrdinalIgnoreCase)
            && sql.Contains("INSERT", StringComparison.OrdinalIgnoreCase)
            && Interlocked.Exchange(ref _fired, 1) == 0)
        {
            throw new TransientTestException($"Lỗi tạm thời giả tại INSERT {table}");
        }
    }

    public override ValueTask<InterceptionResult<DbDataReader>> ReaderExecutingAsync(
        DbCommand command, CommandEventData eventData, InterceptionResult<DbDataReader> result,
        CancellationToken cancellationToken = default)
    {
        MaybeThrow(command);
        return base.ReaderExecutingAsync(command, eventData, result, cancellationToken);
    }

    public override ValueTask<InterceptionResult<int>> NonQueryExecutingAsync(
        DbCommand command, CommandEventData eventData, InterceptionResult<int> result,
        CancellationToken cancellationToken = default)
    {
        MaybeThrow(command);
        return base.NonQueryExecutingAsync(command, eventData, result, cancellationToken);
    }
}

/// <summary>
/// DB25b — quét mã nguồn tìm <c>BeginTransactionAsync</c> KHÔNG được bọc bởi <c>DbRetry.RunAsync</c>.
///
/// <para>Quy ước của repo là mở transaction ngay đầu delegate, nên guard chỉ cần nhìn vài dòng ngay
/// trước đó. Cố ý KHÔNG đếm ngoặc để suy ra khối: các file này có chuỗi nội suy nhiều dòng
/// (<c>$@"INSERT ... {x} ..."</c>) và log template chứa <c>{}</c> — đếm ngoặc trên chúng sẽ sai âm
/// thầm, mà một guard nói dối còn tệ hơn không có guard.</para>
/// </summary>
public static class TransactionSiteScanner
{
    private const int LookBehindLines = 6;

    public static IReadOnlyList<string> FindUnwrapped(string serviceDir)
    {
        var offenders = new List<string>();

        foreach (var file in Directory.EnumerateFiles(serviceDir, "*.cs", SearchOption.AllDirectories))
        {
            if (file.Contains($"{Path.DirectorySeparatorChar}bin{Path.DirectorySeparatorChar}")
                || file.Contains($"{Path.DirectorySeparatorChar}obj{Path.DirectorySeparatorChar}"))
                continue;

            var lines = File.ReadAllLines(file);
            for (var i = 0; i < lines.Length; i++)
            {
                if (IsComment(lines[i]) || !lines[i].Contains("BeginTransactionAsync")) continue;

                var wrapped = false;
                for (var back = Math.Max(0, i - LookBehindLines); back < i; back++)
                    if (lines[back].Contains("DbRetry.RunAsync")) wrapped = true;

                if (!wrapped)
                    offenders.Add($"{Path.GetFileName(file)}:{i + 1}: {lines[i].Trim()}");
            }
        }

        return offenders;
    }

    private static bool IsComment(string line)
    {
        var t = line.TrimStart();
        return t.StartsWith("//") || t.StartsWith("*") || t.StartsWith("/*");
    }
}
