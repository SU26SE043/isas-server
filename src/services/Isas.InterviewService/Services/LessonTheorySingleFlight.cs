using System.Collections.Concurrent;
using Isas.InterviewService.Services.Interfaces;

namespace Isas.InterviewService.Services;

/// <summary>
/// Single-flight sinh lý thuyết bài học theo <c>lessonId</c>: mọi lời gọi đồng thời cho CÙNG một bài
/// (GET của người học · prefetch FE · prewarm nền) chỉ tốn MỘT lượt Gemini, các bên còn lại chờ chung
/// kết quả rồi đọc lại DB.
///
/// Vì sao: đo prod 2026-09-14 — cùng một bài bị sinh HAI lần song song (13:05:15 và 13:05:39, cùng
/// 2.388 prompt token, ~50s và ~$0,025 mỗi lượt); <see cref="RoadmapLessonService"/> khi đó chỉ có
/// <c>ExecuteUpdate</c> điều kiện SAU khi đã gọi AI, tức guard chỉ chặn ghi đè chứ không chặn tiêu tiền.
///
/// Thiết kế CỐ Ý in-process (không cột claim trong DB, không migration): deploy hiện tại là một
/// instance (tiền lệ: rate limiter F17 cũng in-process), lượt sinh là idempotent và on-demand nên
/// restart giữa chừng không mất gì — GET kế tiếp sinh lại. <c>ExecuteUpdate</c> điều kiện vẫn giữ làm
/// guard cuối cho ca nhiều instance (bên thua bỏ kết quả như trước).
///
/// Hai chi tiết bắt buộc:
/// <list type="bullet">
/// <item><c>Lazy&lt;Task&gt;</c> chứ không phải <c>GetOrAdd(id, StartAsync(id))</c>: <c>ConcurrentDictionary</c>
/// có thể gọi factory hai lần khi đua, mà với Task trần thì cả hai thân hàm đều đã chạy (= hai lượt
/// Gemini). <c>ExecutionAndPublication</c> bảo đảm đúng một thân hàm khởi động.</item>
/// <item>Thân hàm chạy trong SCOPE DI RIÊNG với token <c>ApplicationStopping</c>, KHÔNG phải <c>ct</c>
/// của request đầu tiên: người mở bài đóng tab thì chỉ họ thoát (<c>WaitAsync</c>), lượt sinh chung vẫn
/// chạy tới cùng và LƯU — nếu không, một người huỷ là mọi người đang chờ mất trắng ~50s.</item>
/// </list>
/// </summary>
public sealed class LessonTheorySingleFlight
{
    private readonly ConcurrentDictionary<Guid, Lazy<Task<bool>>> _inflight = new();
    private readonly IServiceScopeFactory _scopes;
    private readonly ILogger<LessonTheorySingleFlight> _logger;
    private readonly CancellationToken _stopping;

    public LessonTheorySingleFlight(
        IServiceScopeFactory scopes,
        ILogger<LessonTheorySingleFlight> logger,
        IHostApplicationLifetime? lifetime = null)
    {
        _scopes = scopes;
        _logger = logger;
        _stopping = lifetime?.ApplicationStopping ?? CancellationToken.None;
    }

    /// <summary>Đang có lượt sinh cho bài này (dùng để prewarm khỏi xếp hàng thứ đã chạy).</summary>
    public bool IsInFlight(Guid lessonId) => _inflight.ContainsKey(lessonId);

    /// <summary>
    /// Chạy (hoặc nhập vào) lượt sinh cho <paramref name="lessonId"/>. Trả <c>true</c> khi lượt này
    /// thật sự ghi bài mới, <c>false</c> khi bài đã dùng được / thua đua. Ném đúng exception của lượt
    /// sinh chung (vd <see cref="AiServiceException"/>) cho MỌI bên đang chờ; huỷ <paramref name="waiterCt"/>
    /// chỉ ném <see cref="OperationCanceledException"/> cho bên huỷ.
    /// </summary>
    public Task<bool> RunAsync(Guid lessonId, CancellationToken waiterCt = default)
    {
        var lazy = _inflight.GetOrAdd(lessonId, id => new Lazy<Task<bool>>(
            () => RunDetachedAsync(id), LazyThreadSafetyMode.ExecutionAndPublication));
        return lazy.Value.WaitAsync(waiterCt);
    }

    private async Task<bool> RunDetachedAsync(Guid lessonId)
    {
        try
        {
            using var scope = _scopes.CreateScope();   // DbContext riêng — không dính request nào
            var lessons = scope.ServiceProvider.GetRequiredService<IRoadmapLessonService>();
            return await lessons.GenerateAndPersistAsync(lessonId, _stopping);
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Single-flight: lượt sinh lý thuyết lesson {LessonId} lỗi — gỡ khỏi bảng để lần sau sinh lại", lessonId);
            throw;
        }
        finally
        {
            // Lỗi cũng gỡ: giữ lại là mọi lời gọi sau nhập vào một Task đã faulted và nhận mãi cùng lỗi.
            _inflight.TryRemove(lessonId, out _);
        }
    }
}
