using Isas.CampaignService.Models;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Options;

namespace Isas.CampaignService.Services
{
    /// <summary>
    /// BK25 — RETENTION cho ảnh sinh trắc học trong S3 (DATA-3). Quét <c>face_images</c>, ảnh quá
    /// <c>RetentionDays</c> thì xoá object SeaweedFS rồi xoá dòng sổ.
    ///
    /// ⚠ ĐÂY LÀ JOB XOÁ DỮ LIỆU SINH TRẮC HỌC — thứ nhạy cảm nhất hệ thống đang giữ, và là bằng
    /// chứng của một buổi thi. Ba lớp chặn, cả ba bắt buộc (mẫu <see cref="OutboxPurger"/>):
    ///  1. <c>Enabled</c> — tắt được hoàn toàn bằng config, và MẶC ĐỊNH TẮT.
    ///  2. Chỉ đụng dòng có <c>captured_at &lt; cutoff</c>. Ảnh còn trong hạn không bao giờ bị chạm.
    ///  3. Trần <c>BatchSize</c> mỗi vòng + log số ảnh đã xoá.
    ///
    /// <b>THỨ TỰ XOÁ: object S3 TRƯỚC → dòng DB SAU.</b> Không phải tuỳ chọn phong cách:
    ///  • S3 xong, DB hỏng → dòng còn trỏ vào object đã mất; vòng sau DeleteObject lên key vắng mặt
    ///    là no-op rồi dọn nốt dòng ⇒ tự lành, vô hại.
    ///  • DB xong, S3 hỏng → ảnh khuôn mặt nằm lại S3 mà KHÔNG còn gì trỏ tới ⇒ tái tạo nguyên xi
    ///    con bug BK25 sinh ra để diệt, lần này không ai biết mà dọn.
    /// Cùng lập luận (và cùng chiều) với <c>KnowledgeService.DeleteAsync</c> bên InterviewService:
    /// xoá kho ngoài trước, metadata sau.
    ///
    /// Xoá ảnh THAM CHIẾU còn kéo theo việc gỡ con trỏ <c>campaign_membership.reference_image_key</c>
    /// — để trơ lại thì DB khẳng định "có ảnh tham chiếu" trong khi object đã mất, và
    /// <c>face-check</c> sẽ gửi một key chết sang AIService thay vì đi nhánh thật thà
    /// <c>identity_unverified</c>. Gỡ con trỏ có GUARD bằng chính key vừa xoá — xem trong hàm.
    /// </summary>
    public class FaceImagePurger : BackgroundService
    {
        private readonly IServiceScopeFactory _scopeFactory;
        private readonly FaceImageRetentionSettings _options;
        private readonly ILogger<FaceImagePurger> _logger;

        public FaceImagePurger(
            IServiceScopeFactory scopeFactory,
            IOptions<FaceImageRetentionSettings> options,
            ILogger<FaceImagePurger> logger)
        {
            _scopeFactory = scopeFactory;
            _options = options.Value;
            _logger = logger;
        }

        protected override async Task ExecuteAsync(CancellationToken ct)
        {
            await Task.Delay(TimeSpan.FromSeconds(30), ct);   // nhường khởi động; purge không gấp

            var interval = TimeSpan.FromSeconds(
                _options.ScanIntervalSeconds > 0 ? _options.ScanIntervalSeconds : 3600);

            while (!ct.IsCancellationRequested)
            {
                try
                {
                    await PurgeOnceAsync(ct);
                }
                catch (Exception ex)
                {
                    _logger.LogError(ex, "Lỗi khi dọn ảnh sinh trắc quá hạn (face_images)");
                }

                await Task.Delay(interval, ct);
            }
        }

        // private + gọi qua reflection trong test (idiom repo: OutboxPurger/OutboxDispatcher).
        private async Task<int> PurgeOnceAsync(CancellationToken ct)
        {
            if (!_options.Enabled) return 0;   // lớp chặn 1: tắt được bằng config, mặc định TẮT

            var retentionDays = _options.RetentionDays > 0 ? _options.RetentionDays : 90;
            var batch = _options.BatchSize > 0 ? _options.BatchSize : 200;
            var cutoff = DateTime.UtcNow.AddDays(-retentionDays);

            using var scope = _scopeFactory.CreateScope();
            var db = scope.ServiceProvider.GetRequiredService<CampaignDbContext>();
            var files = scope.ServiceProvider.GetRequiredService<IFileService>();

            // Lớp chặn 2 — điều kiện xoá, KHÔNG được nới: ảnh chưa quá hạn giữ thì tuyệt đối không chạm.
            var rows = await db.FaceImages
                .Where(x => x.CapturedAt < cutoff)
                .OrderBy(x => x.CapturedAt)               // cũ nhất trước — dọn đều, không bỏ sót đuôi
                .Take(batch)                              // lớp chặn 3: trần mỗi vòng
                .ToListAsync(ct);

            if (rows.Count == 0) return 0;

            // Bước 1 — xoá object S3 TRƯỚC. Object nào xoá lỗi thì GIỮ NGUYÊN dòng sổ của nó
            // (không đưa vào `purged`) → vòng sau thử lại. Thà retry mãi còn hơn mất dấu vết.
            var purged = new List<FaceImage>(rows.Count);
            foreach (var row in rows)
            {
                try
                {
                    await files.DeleteAsync(row.StorageKey, ct);
                }
                catch (Exception ex)
                {
                    _logger.LogWarning(ex,
                        "FaceImagePurger: xoá object S3 '{Key}' thất bại — GIỮ dòng sổ để vòng sau thử lại",
                        row.StorageKey);
                    continue;
                }
                purged.Add(row);
            }

            if (purged.Count == 0) return 0;

            // Bước 2 — gỡ con trỏ ảnh THAM CHIẾU. Guard `ReferenceImageKey == row.StorageKey` là bắt
            // buộc: ứng viên có thể đã enroll lại bằng ảnh MỚI (key khác vì đổi đuôi file) trong lúc
            // bản cũ chờ quá hạn; thiếu guard sẽ xoá trắng ảnh tham chiếu ĐANG DÙNG.
            // IgnoreQueryFilters: membership có filter soft-delete theo Campaign (DB13), mà campaign
            // đã soft-delete chính là nhóm cần dọn nhất — không bỏ filter thì con trỏ ở đó nằm trơ.
            foreach (var row in purged.Where(x => x.Kind == FaceImageKind.Reference))
            {
                await db.CampaignMemberships
                    .IgnoreQueryFilters()
                    .Where(m => m.CampaignId == row.CampaignId
                        && m.CandidateId == row.CandidateId
                        && m.ReferenceImageKey == row.StorageKey)
                    .ExecuteUpdateAsync(s => s
                        .SetProperty(m => m.ReferenceImageKey, (string?)null)
                        // DB14: đường ExecuteUpdate bỏ qua change tracker nên phải tự đóng dấu updated_at.
                        .SetProperty(m => m.UpdatedAt, DateTime.UtcNow), ct);
            }

            // Bước 3 — dọn dòng sổ của những object đã thực sự biến mất khỏi S3.
            var ids = purged.Select(p => p.Id).ToList();
            var deleted = await db.FaceImages
                .Where(x => ids.Contains(x.Id))
                .ExecuteDeleteAsync(ct);

            _logger.LogInformation(
                "FaceImagePurger: đã xoá {Deleted} ảnh sinh trắc chụp trước {Cutoff:u} (giữ {Days} ngày, DATA-3/CAMP-13)",
                deleted, cutoff, retentionDays);

            return deleted;
        }
    }
}
