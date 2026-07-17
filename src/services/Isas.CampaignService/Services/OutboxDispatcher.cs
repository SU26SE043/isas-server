using Isas.CampaignService.Models;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Options;

namespace Isas.CampaignService.Services
{
    /// <summary>
    /// DB2b — Transactional Outbox DISPATCHER cho invitation-email. Quét <c>outbox_messages</c>
    /// (<c>published_at IS NULL</c>), publish payload NGUYÊN lên queue
    /// <c>campaign_invitation_email_queue</c> (<c>MessageId = Id</c>), rồi set <c>published_at</c>.
    /// At-least-once: publish 1 row lỗi (broker down) → giữ <c>published_at</c> null + <c>Attempts++</c> →
    /// vòng sau gửi lại (mail KHÔNG mất). Consumer idempotent theo <c>email_sent_at</c> (redeliver → không
    /// gửi trùng), nên phát lại KHÔNG double-send.
    ///
    /// Thay "publish best-effort SAU SaveChanges" cũ (dual-write: mất mail khi broker chết giữa 2 lần
    /// SaveChanges tạo lời mời). ĐÂY là đường DUY NHẤT publish invitation-email (nơi tạo lời mời chỉ ghi row).
    ///
    /// Mirror InterviewService.OutboxDispatcher + StuckScreeningRepublisher (Campaign): options on/off +
    /// interval, delay 1 nhịp trước lần quét đầu, try/catch quanh mỗi vòng (1 lỗi KHÔNG giết service), scope
    /// riêng/lần quét cho DbContext (BackgroundService = singleton), publisher singleton inject thẳng.
    /// </summary>
    public class OutboxDispatcher : BackgroundService
    {
        private readonly IServiceScopeFactory _scopeFactory;
        private readonly IInvitationEmailPublisher _publisher;   // singleton — inject thẳng được
        private readonly OutboxSettings _options;
        private readonly ILogger<OutboxDispatcher> _logger;

        public OutboxDispatcher(
            IServiceScopeFactory scopeFactory,
            IInvitationEmailPublisher publisher,
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
                    _logger.LogError(ex, "Lỗi khi phát outbox invitation-email");
                }

                await Task.Delay(interval, ct);
            }
        }

        // private + gọi qua reflection trong test (idiom repo: StuckScreeningRepublisher/SessionScoredConsumer).
        private async Task ScanOnceAsync(CancellationToken ct)
        {
            if (!_options.Enabled) return;   // safe-disable

            // BackgroundService = singleton → tạo scope riêng cho DbContext (scoped) mỗi vòng.
            using var scope = _scopeFactory.CreateScope();
            var db = scope.ServiceProvider.GetRequiredService<CampaignDbContext>();

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
                    await _publisher.PublishRawAsync(m.Payload, m.Id.ToString(), ct);
                    m.PublishedAt = now;
                    sent++;
                }
                catch (Exception ex)
                {
                    // Broker down / publish lỗi → giữ published_at null + đếm số lần thử → vòng sau gửi lại.
                    m.Attempts++;
                    _logger.LogError(ex,
                        "Phát outbox-row {MessageId} (invitation {InvitationId}) thất bại, để vòng sau (attempts={Attempts})",
                        m.Id, m.InvitationId, m.Attempts);
                }
            }

            await db.SaveChangesAsync(ct);

            if (sent > 0)
                _logger.LogInformation("OutboxDispatcher: đã phát {Sent}/{Total} invitation-email", sent, pending.Count);
        }
    }
}
