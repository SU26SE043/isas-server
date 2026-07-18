using Isas.CampaignService.Models;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Options;

namespace Isas.CampaignService.Services
{
    /// <summary>
    /// DB28 — RETENTION cho <c>outbox_messages</c>. Trước đây row đã publish không bao giờ được xoá:
    /// bảng phình vô hạn (1 row/lời mời từng gửi, payload jsonb). Partial index
    /// <c>WHERE published_at IS NULL</c> vẫn giữ dispatcher nhanh nên hỏng ÂM THẦM — không lộ ở
    /// latency, chỉ lộ ở đĩa/autovacuum/thời gian backup.
    ///
    /// ⚠ ĐÂY LÀ JOB XOÁ DỮ LIỆU. Ba lớp chặn, cả ba đều bắt buộc:
    ///  1. <c>PurgeEnabled</c> — tắt được hoàn toàn bằng config.
    ///  2. Chỉ đụng row <c>published_at IS NOT NULL</c> VÀ đã quá <c>PurgeRetentionDays</c>.
    ///     Row chưa publish = mail CHƯA GỬI → tuyệt đối không xoá, bất kể cũ đến đâu.
    ///  3. Trần <c>PurgeBatchSize</c> mỗi vòng (transaction ngắn) + log số row đã xoá.
    ///
    /// Row bị xoá là rác thuần: mail đã gửi, và dedup chống gửi trùng nằm ở
    /// <c>campaign_invitations.email_sent_at</c> chứ không phải ở outbox-row. Xoá còn là điểm cộng
    /// bảo mật — payload mang token mời THÔ (DB23 chỉ hash bản trong bảng invitation).
    ///
    /// Mirror shape OutboxDispatcher (options on/off + interval, delay 1 nhịp, try/catch mỗi vòng,
    /// scope riêng cho DbContext vì BackgroundService là singleton).
    /// </summary>
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
            await Task.Delay(TimeSpan.FromSeconds(30), ct);   // nhường khởi động; purge không gấp

            var interval = TimeSpan.FromSeconds(
                _options.PurgeIntervalSeconds > 0 ? _options.PurgeIntervalSeconds : 3600);

            while (!ct.IsCancellationRequested)
            {
                try
                {
                    await PurgeOnceAsync(ct);
                }
                catch (Exception ex)
                {
                    _logger.LogError(ex, "Lỗi khi dọn outbox_messages đã publish");
                }

                await Task.Delay(interval, ct);
            }
        }

        // private + gọi qua reflection trong test (idiom repo: OutboxDispatcher/StuckScreeningRepublisher).
        private async Task<int> PurgeOnceAsync(CancellationToken ct)
        {
            if (!_options.PurgeEnabled) return 0;   // lớp chặn 1: tắt được bằng config

            var retentionDays = _options.PurgeRetentionDays > 0 ? _options.PurgeRetentionDays : 30;
            var batch = _options.PurgeBatchSize > 0 ? _options.PurgeBatchSize : 500;
            var cutoff = DateTime.UtcNow.AddDays(-retentionDays);

            using var scope = _scopeFactory.CreateScope();
            var db = scope.ServiceProvider.GetRequiredService<CampaignDbContext>();

            // Lớp chặn 2 — điều kiện xoá, KHÔNG được nới:
            //   PublishedAt != null  → row chưa gửi được thì giữ mãi (dispatcher còn phải retry).
            //   PublishedAt < cutoff → chỉ rác đã quá hạn giữ.
            // (`!= null` về mặt logic là THỪA: `published_at < cutoff` với NULL cho UNKNOWN nên row
            //  chưa publish đã tự rớt — mutation test xác nhận gỡ nó ra vẫn xanh. Giữ lại vì đây là
            //  điều kiện an toàn quan trọng nhất của job, viết tường minh để người sửa sau thấy ngay
            //  ý định, thay vì phải suy ra từ logic 3 trị của SQL.)
            // Chọn Id trước rồi mới xoá theo Id: trần batch được áp TƯỜNG MINH và giống nhau trên
            // Postgres lẫn SQLite (ExecuteDelete + Take không dịch đồng nhất giữa 2 provider).
            var ids = await db.OutboxMessages
                .Where(m => m.PublishedAt != null && m.PublishedAt < cutoff)
                .OrderBy(m => m.PublishedAt)         // cũ nhất trước — dọn đều, không bỏ sót đuôi
                .Select(m => m.Id)
                .Take(batch)                          // lớp chặn 3: trần mỗi vòng
                .ToListAsync(ct);

            if (ids.Count == 0) return 0;

            var deleted = await db.OutboxMessages
                .Where(m => ids.Contains(m.Id))
                .ExecuteDeleteAsync(ct);

            _logger.LogInformation(
                "OutboxPurger: đã xoá {Deleted} outbox-row đã publish trước {Cutoff:u} (giữ {Days} ngày)",
                deleted, cutoff, retentionDays);

            return deleted;
        }
    }
}
