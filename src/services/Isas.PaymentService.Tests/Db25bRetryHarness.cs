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
/// S11-CATCH — chen MỘT lần vào ngay SAU câu lệnh đầu tiên khớp <paramref name="match"/>, để dựng lại
/// đúng khoảnh khắc "đối thủ vừa commit xen vào giữa lúc ta ĐỌC và lúc ta GHI".
///
/// <para>Khác <see cref="ThrowOnceInterceptor"/> (giả lỗi tạm thời), cái này tạo ra một cuộc ĐUA THẬT:
/// dữ liệu trong DB thay đổi thật giữa hai câu lệnh, nên hậu kiểm sau <c>catch</c> nhìn thấy trạng thái
/// thật chứ không phải trạng thái do test dựng sẵn.</para>
///
/// <para>Chạy ở <c>*ExecutedAsync</c> (SAU câu lệnh) chứ không phải <c>*ExecutingAsync</c>: mục đích là
/// để lời gọi đọc kịp thấy dữ liệu CŨ rồi đi tiếp trên tiền đề đã lỗi thời — đúng cửa sổ đua thật.</para>
/// </summary>
public sealed class RaceOnceInterceptor(Func<string, bool> match, Func<DbCommand, Task> race)
    : DbCommandInterceptor
{
    private int _fired;

    /// <summary>Đã chen được chưa — test PHẢI assert, nếu không phép thử là rỗng.</summary>
    public bool Fired => Volatile.Read(ref _fired) > 0;

    private async Task MaybeRaceAsync(DbCommand command)
    {
        if (!match(command.CommandText)) return;
        if (Interlocked.Exchange(ref _fired, 1) != 0) return;
        await race(command);
    }

    public override async ValueTask<DbDataReader> ReaderExecutedAsync(
        DbCommand command, CommandExecutedEventData eventData, DbDataReader result,
        CancellationToken cancellationToken = default)
    {
        await MaybeRaceAsync(command);
        return await base.ReaderExecutedAsync(command, eventData, result, cancellationToken);
    }

    public override async ValueTask<object?> ScalarExecutedAsync(
        DbCommand command, CommandExecutedEventData eventData, object? result,
        CancellationToken cancellationToken = default)
    {
        await MaybeRaceAsync(command);
        return await base.ScalarExecutedAsync(command, eventData, result, cancellationToken);
    }
}

/// <summary>
/// S11-CATCH — quét mã nguồn tìm <c>catch</c> bắt <c>DbUpdateException</c> mà KHÔNG có đường thoát nào:
/// không <c>throw;</c> (đẩy lỗi lên cho caller, và để execution strategy còn thấy lỗi tạm thời mà thử
/// lại) và cũng không <c>LogError</c> (ghi lại để còn đối soát tay). Nuốt sạch cả hai đường = một lần
/// ghi mất tích không dấu vết — đúng lớp lỗi đã làm khách trả tiền mà đơn kẹt <c>Pending</c> (DB20).
///
/// <para>Bắt được CẢ hai dạng khai báo: <c>catch (DbUpdateException)</c> và
/// <c>catch (Exception ex) when (ex is DbUpdateException or …)</c> — chỉ cần dòng <c>catch</c> có nhắc
/// tới tên kiểu.</para>
///
/// <para>Phạm vi khối được xác định bằng dấu <c>}</c> ĐẦU TIÊN có cùng mức thụt đầu dòng với
/// <c>catch</c> — cố ý KHÔNG đếm ngoặc (chuỗi nội suy nhiều dòng và log template chứa <c>{}</c> sẽ làm
/// phép đếm sai âm thầm, mà một guard nói dối còn tệ hơn không có guard). Không tìm thấy dấu đóng
/// trong <see cref="MaxBlockLines"/> dòng ⇒ BÁO LÀ VI PHẠM chứ không im lặng cho qua.</para>
/// </summary>
public static class CatchSwallowScanner
{
    private const int MaxBlockLines = 60;

    public static IReadOnlyList<string> FindSilentSwallows(string serviceDir)
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
                if (IsComment(lines[i])) continue;
                if (!lines[i].Contains("catch") || !lines[i].Contains("DbUpdateException")) continue;

                if (!HasEscapeHatch(lines, i))
                    offenders.Add($"{Path.GetFileName(file)}:{i + 1}: {lines[i].Trim()}");
            }
        }

        return offenders;
    }

    private static bool HasEscapeHatch(string[] lines, int catchLine)
    {
        var closing = new string(' ', lines[catchLine].Length - lines[catchLine].TrimStart().Length) + "}";

        for (var i = catchLine + 1; i < lines.Length && i <= catchLine + MaxBlockLines; i++)
        {
            if (lines[i].TrimEnd() == closing) return false;   // hết khối mà không thấy đường thoát
            if (IsComment(lines[i])) continue;
            if (lines[i].Contains("throw;") || lines[i].Contains("LogError")) return true;
        }

        return false;   // không xác định được khối ⇒ coi là vi phạm, không im lặng cho qua
    }

    private static bool IsComment(string line)
    {
        var t = line.TrimStart();
        return t.StartsWith("//") || t.StartsWith("*") || t.StartsWith("/*");
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
