using Isas.InterviewService.ApplicationDbContext;
using Isas.InterviewService.Models;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Options;

namespace Isas.InterviewService.Services;

// DB28 — RETENTION cho outbox_messages. Trước đây KHÔNG có purge ở đâu: row đã publish nằm lại vĩnh
// viễn. Partial index `WHERE published_at IS NULL` khiến OutboxDispatcher vẫn nhanh dù bảng phình →
// triệu chứng KHÔNG hiện ra ở latency mà ở đĩa / autovacuum / thời gian backup, tức là hỏng âm thầm.
//
// ⚠ ĐÂY LÀ XOÁ DỮ LIỆU. 3 rào an toàn, cả 3 đều bắt buộc:
//   1. Công tắc riêng `Outbox:PurgeEnabled` (tắt được mà không đụng dispatcher).
//   2. CHỈ xoá row `published_at IS NOT NULL` **và** cũ hơn hạn giữ. Row chưa publish là event CHƯA
//      tới Payment/Campaign — xoá nó = mất tiền/mất mail, nên nó tuyệt đối nằm ngoài predicate,
//      bất kể cũ đến đâu (broker chết 3 tháng thì row vẫn phải còn để gửi lại).
//   3. Xoá theo batch có trần (PurgeBatchSize × PurgeMaxBatchesPerScan) → không DELETE khối lớn
//      khoá bảng/phình WAL; phần dư để vòng sau.
// Retention ≤ 0 = TẮT (không diễn giải thành "hết hạn ngay" — đọc nhầm 1 config không được phép
// trở thành lệnh xoá sạch bảng).
public class OutboxPurger : BackgroundService
{
    private readonly IServiceScopeFactory _scopeFactory;
    private readonly OutboxSettings _options;
    private readonly ILogger<OutboxPurger> _logger;

    public OutboxPurger(
        IServiceScopeFactory scopeFactory,
        IOptions<OutboxSettings> options,
        ILogger<OutboxPurger> logger)
    {
        _scopeFactory = scopeFactory;
        _options = options.Value;
        _logger = logger;
    }

    protected override async Task ExecuteAsync(CancellationToken ct)
    {
        // Chờ app khởi động xong; dọn rác không tranh nhịp với dispatcher lúc boot.
        await Task.Delay(TimeSpan.FromMinutes(1), ct);

        var interval = TimeSpan.FromMinutes(
            _options.PurgeIntervalMinutes > 0 ? _options.PurgeIntervalMinutes : 60);

        while (!ct.IsCancellationRequested)
        {
            try
            {
                await PurgeOnceAsync(ct);
            }
            catch (Exception ex)
            {
                // 1 vòng lỗi KHÔNG giết background service (mẫu OutboxDispatcher/StuckAnswerRepublisher).
                _logger.LogError(ex, "Lỗi khi dọn outbox_messages đã phát");
            }

            await Task.Delay(interval, ct);
        }
    }

    // private + gọi qua reflection trong test (idiom repo: OutboxDispatcher/StuckAnswerRepublisher).
    private async Task PurgeOnceAsync(CancellationToken ct)
    {
        if (!_options.PurgeEnabled) return;                 // rào 1: công tắc
        if (_options.PurgeRetentionDays <= 0) return;       // rào 1: retention vô nghĩa = tắt, KHÔNG xoá

        using var scope = _scopeFactory.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<InterviewDbContext>();

        var cutoff = DateTime.UtcNow.AddDays(-_options.PurgeRetentionDays);
        var batchSize = _options.PurgeBatchSize > 0 ? _options.PurgeBatchSize : 1000;
        var maxBatches = _options.PurgeMaxBatchesPerScan > 0 ? _options.PurgeMaxBatchesPerScan : 10;

        var deleted = 0;
        for (var i = 0; i < maxBatches; i++)
        {
            // Rào 2: predicate DUY NHẤT — đã publish VÀ quá hạn. Lấy id trước rồi xoá theo id thay vì
            // ExecuteDelete kèm Take (DELETE…LIMIT không portable) — vẫn bounded, chạy được cả Postgres
            // lẫn SQLite (test).
            var ids = await db.OutboxMessages
                .Where(m => m.PublishedAt != null && m.PublishedAt < cutoff)
                .OrderBy(m => m.PublishedAt)     // cũ nhất trước
                .Select(m => m.Id)
                .Take(batchSize)                 // rào 3: trần mỗi batch
                .ToListAsync(ct);

            if (ids.Count == 0) break;

            deleted += await db.OutboxMessages
                .Where(m => ids.Contains(m.Id))
                .ExecuteDeleteAsync(ct);

            if (ids.Count < batchSize) break;    // hết hàng, khỏi quay vòng nữa
        }

        if (deleted > 0)
            _logger.LogInformation(
                "OutboxPurger: đã xoá {Deleted} outbox-row đã phát cũ hơn {Days} ngày (mốc {Cutoff:o})",
                deleted, _options.PurgeRetentionDays, cutoff);
    }
}
