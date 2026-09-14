using System.Collections.Concurrent;
using System.Threading.Channels;
using Isas.InterviewService.Models;
using Microsoft.Extensions.Options;

namespace Isas.InterviewService.Services;

/// <summary>
/// Hàng đợi in-memory các <c>lessonId</c> cần sinh lý thuyết NỀN (xem <see cref="LessonPrewarmOptions"/>).
/// Singleton; <see cref="LessonTheoryPrewarmer"/> đọc tuần tự. Dedupe theo id + bỏ qua bài đang in-flight
/// ở <see cref="LessonTheorySingleFlight"/> ⇒ không xếp hàng thứ đã đang chạy. Đầy → bỏ (không chặn
/// request đang enqueue): prewarm là tối ưu, mất một lượt prewarm chỉ đưa về hành vi cũ (sinh khi mở).
/// </summary>
public sealed class LessonPrewarmQueue
{
    private readonly Channel<Guid> _channel;
    private readonly ConcurrentDictionary<Guid, byte> _queued = new();
    private readonly LessonTheorySingleFlight? _singleFlight;
    private readonly ILogger<LessonPrewarmQueue> _logger;

    public LessonPrewarmQueue(
        IOptions<LessonPrewarmOptions>? options = null,
        LessonTheorySingleFlight? singleFlight = null,
        ILogger<LessonPrewarmQueue>? logger = null)
    {
        var opts = options?.Value ?? new LessonPrewarmOptions();
        Enabled = opts.Enabled;
        // FullMode = Wait (mặc định) CỐ Ý: `TryWrite` khi đầy trả FALSE để gỡ id khỏi set dedupe.
        // ⚠ KHÔNG dùng DropWrite — chế độ đó làm `TryWrite` trả TRUE rồi lặng lẽ vứt item: id kẹt trong
        // `_queued` mà không bao giờ được xử lý, và từ đó bài KHÔNG xếp hàng lại được (test Queue_Day_BoKhongChan
        // bắt được đúng ca này). Ở đây chỉ dùng TryWrite nên Wait không bao giờ chặn.
        _channel = Channel.CreateBounded<Guid>(new BoundedChannelOptions(Math.Max(1, opts.QueueCapacity))
        {
            FullMode = BoundedChannelFullMode.Wait,
            SingleReader = true,
        });
        _singleFlight = singleFlight;
        _logger = logger ?? Microsoft.Extensions.Logging.Abstractions.NullLogger<LessonPrewarmQueue>.Instance;
    }

    public bool Enabled { get; }

    public ChannelReader<Guid> Reader => _channel.Reader;

    /// <summary>Số bài đang chờ (test/quan sát).</summary>
    public int PendingCount => _queued.Count;

    /// <summary>Xếp hàng sinh nền. <c>false</c> = tắt / đã xếp / đang in-flight / hàng đợi đầy.</summary>
    public bool TryEnqueue(Guid lessonId)
    {
        if (!Enabled) return false;
        if (_singleFlight?.IsInFlight(lessonId) == true) return false;
        if (!_queued.TryAdd(lessonId, 0)) return false;
        if (_channel.Writer.TryWrite(lessonId)) return true;

        _queued.TryRemove(lessonId, out _);
        _logger.LogDebug("Prewarm: hàng đợi đầy, bỏ lesson {LessonId} (sẽ sinh khi người học mở)", lessonId);
        return false;
    }

    /// <summary>Prewarmer gọi ngay khi lấy id ra khỏi hàng — từ đây bài có thể được xếp lại.</summary>
    public void MarkDequeued(Guid lessonId) => _queued.TryRemove(lessonId, out _);
}
