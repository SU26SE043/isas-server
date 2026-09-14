using System.Diagnostics;

namespace Isas.InterviewService.Services;

/// <summary>
/// Sinh lý thuyết bài học NỀN từ <see cref="LessonPrewarmQueue"/>, tuần tự (concurrency = 1 — mỗi lượt là
/// một lời gọi Gemini 20–50s; chạy song song là tự tranh tài nguyên với chính GET của người học).
/// Mọi lượt đi qua <see cref="LessonTheorySingleFlight"/> ⇒ người học mở đúng bài đang prewarm thì nhập vào
/// lượt đó, không tốn thêm. Lỗi → warn + bỏ qua, KHÔNG retry, KHÔNG enqueue tiếp (không cascade): GET của
/// người học sinh lại on-demand như trước. Restart mất hàng đợi in-memory — chấp nhận, cùng lý do.
/// </summary>
public sealed class LessonTheoryPrewarmer : BackgroundService
{
    private readonly LessonPrewarmQueue _queue;
    private readonly LessonTheorySingleFlight _singleFlight;
    private readonly ILogger<LessonTheoryPrewarmer> _logger;

    public LessonTheoryPrewarmer(
        LessonPrewarmQueue queue,
        LessonTheorySingleFlight singleFlight,
        ILogger<LessonTheoryPrewarmer> logger)
    {
        _queue = queue;
        _singleFlight = singleFlight;
        _logger = logger;
    }

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        if (!_queue.Enabled)
        {
            _logger.LogInformation("Prewarm lý thuyết bài học: TẮT (LessonPrewarm:Enabled=false)");
            return;
        }

        try
        {
            while (await _queue.Reader.WaitToReadAsync(stoppingToken))
                await ProcessQueuedAsync(stoppingToken);
        }
        catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested)
        {
            // shutdown bình thường
        }
    }

    /// <summary>Rút hết những gì đang có trong hàng và sinh tuần tự. internal = test hook.</summary>
    internal async Task ProcessQueuedAsync(CancellationToken ct)
    {
        while (_queue.Reader.TryRead(out var lessonId))
        {
            _queue.MarkDequeued(lessonId);
            var sw = Stopwatch.StartNew();
            try
            {
                var generated = await _singleFlight.RunAsync(lessonId, ct);
                _logger.LogInformation("[⏱] lesson-prewarm lesson={LessonId} generated={Generated} elapsed={Elapsed}ms",
                    lessonId, generated, sw.ElapsedMilliseconds);
            }
            catch (OperationCanceledException) when (ct.IsCancellationRequested)
            {
                throw;
            }
            catch (Exception ex)
            {
                _logger.LogWarning(ex, "[⏱] lesson-prewarm lesson={LessonId} outcome=error elapsed={Elapsed}ms — bỏ qua, người học mở bài sẽ sinh lại",
                    lessonId, sw.ElapsedMilliseconds);
            }
        }
    }
}
