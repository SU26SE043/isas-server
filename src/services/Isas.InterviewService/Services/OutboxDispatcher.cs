using Isas.InterviewService.ApplicationDbContext;
using Isas.InterviewService.Models;
using Isas.InterviewService.Services.Interfaces;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Options;

namespace Isas.InterviewService.Services;

// DB2 — Transactional Outbox DISPATCHER. Quét outbox_messages (published_at IS NULL), publish lên
// exchange "interview.events" (routing key = Type, body = Payload nguyên, MessageId = Id), rồi set
// published_at. At-least-once: publish 1 row lỗi (broker down) → giữ published_at null + Attempts++ →
// vòng sau gửi lại (event KHÔNG mất). Consumer Payment idempotent theo session_id (PAY-11) nên phát
// lại KHÔNG double-consume/refund.
//
// Thay "publish best-effort SAU SaveChanges" cũ (mất event khi broker chết lúc đóng session) +
// SettlementReconciler (chỉ backfill B2C, bỏ sót B2B + generation_failed). Outbox phủ CẢ B2C, B2B và
// generation_failed. ĐÂY là đường DUY NHẤT publish settlement-event (nơi đóng session chỉ ghi row).
//
// Mirror CreditReservationReconciler (DB4)/SettlementReconciler: options on/off + interval, delay 1 nhịp
// trước lần quét đầu, try/catch quanh mỗi vòng (1 lỗi KHÔNG giết service), scope riêng/lần quét cho
// DbContext (BackgroundService = singleton), publisher singleton inject thẳng.
public class OutboxDispatcher : BackgroundService
{
    private readonly IServiceScopeFactory _scopeFactory;
    private readonly ISessionEventPublisher _publisher;   // singleton, inject thẳng được
    private readonly OutboxSettings _options;
    private readonly ILogger<OutboxDispatcher> _logger;

    public OutboxDispatcher(
        IServiceScopeFactory scopeFactory,
        ISessionEventPublisher publisher,
        IOptions<OutboxSettings> options,
        ILogger<OutboxDispatcher> logger)
    {
        _scopeFactory = scopeFactory;
        _publisher = publisher;
        _options = options.Value;
        _logger = logger;
    }

    protected override async Task ExecuteAsync(CancellationToken ct)
    {
        // Chờ 1 nhịp cho app khởi động xong trước khi quét lần đầu.
        await Task.Delay(TimeSpan.FromSeconds(5), ct);

        var interval = TimeSpan.FromSeconds(_options.ScanIntervalSeconds > 0 ? _options.ScanIntervalSeconds : 15);

        while (!ct.IsCancellationRequested)
        {
            try
            {
                await ScanOnceAsync(ct);
            }
            catch (Exception ex)
            {
                // Không để 1 vòng lỗi giết cả background service.
                _logger.LogError(ex, "Lỗi khi phát outbox settlement-event");
            }

            await Task.Delay(interval, ct);
        }
    }

    // private + gọi qua reflection trong test (idiom repo: SettlementReconciler/StuckAnswerRepublisher).
    private async Task ScanOnceAsync(CancellationToken ct)
    {
        if (!_options.Enabled) return;   // safe-disable

        // BackgroundService = singleton → tạo scope riêng cho DbContext (scoped) mỗi vòng.
        using var scope = _scopeFactory.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<InterviewDbContext>();

        var batch = _options.BatchSize > 0 ? _options.BatchSize : 100;

        // Row chưa gửi, theo thứ tự xảy ra (giữ thứ tự phát). Tracked (batch nhỏ) → set trực tiếp rồi
        // SaveChanges 1 lần cuối vòng: publish OK → published_at; publish lỗi → Attempts++ (giữ null).
        var pending = await db.OutboxMessages
            .Where(m => m.PublishedAt == null)
            .OrderBy(m => m.OccurredAt)
            .Take(batch)
            .ToListAsync(ct);

        if (pending.Count == 0) return;

        var now = DateTime.UtcNow;
        var sent = 0;

        foreach (var m in pending)
        {
            try
            {
                await _publisher.PublishRawAsync(m.Type, m.Payload, m.Id.ToString(), ct);
                m.PublishedAt = now;
                sent++;
            }
            catch (Exception ex)
            {
                // Broker down / publish lỗi → giữ published_at null + đếm số lần thử → vòng sau gửi lại.
                m.Attempts++;
                _logger.LogError(ex,
                    "Phát outbox-row {MessageId} ({Type}, session {SessionId}) thất bại, để vòng sau (attempts={Attempts})",
                    m.Id, m.Type, m.SessionId, m.Attempts);
            }
        }

        await db.SaveChangesAsync(ct);

        if (sent > 0)
            _logger.LogInformation("OutboxDispatcher: đã phát {Sent}/{Total} settlement-event", sent, pending.Count);
    }
}
