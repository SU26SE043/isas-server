using Isas.InterviewService.ApplicationDbContext;
using Isas.InterviewService.Models;
using Isas.InterviewService.Services.Interfaces;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Options;

namespace Isas.InterviewService.Services;

/// <summary>
/// B2C coaching (2026-09-17) — RETENTION cho ảnh webcam trong S3 (DATA-3, mirror
/// <c>CampaignService.FaceImagePurger</c> của BK25, bớt bước gỡ con trỏ THAM CHIẾU — B2C không có
/// khái niệm ảnh tham chiếu/enroll, chỉ ảnh LIVE mỗi lượt kiểm mặt).
///
/// THỨ TỰ XOÁ: object S3 TRƯỚC → dòng DB SAU (cùng lập luận <c>FaceImagePurger</c>/
/// <c>KnowledgeService.DeleteAsync</c>): xoá dòng trước thì object còn lại trong S3 mà không gì
/// trỏ tới — tái tạo đúng con bug BK25 sinh ra để diệt.
/// </summary>
public class PracticeFaceImagePurger : BackgroundService
{
    private readonly IServiceScopeFactory _scopeFactory;
    private readonly FaceImageRetentionSettings _options;
    private readonly ILogger<PracticeFaceImagePurger> _logger;

    public PracticeFaceImagePurger(
        IServiceScopeFactory scopeFactory,
        IOptions<FaceImageRetentionSettings> options,
        ILogger<PracticeFaceImagePurger> logger)
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
                _logger.LogError(ex, "Lỗi khi dọn ảnh webcam quá hạn (practice_face_images)");
            }

            await Task.Delay(interval, ct);
        }
    }

    // private + gọi qua reflection trong test (idiom repo: OutboxPurger/OutboxDispatcher/FaceImagePurger).
    private async Task<int> PurgeOnceAsync(CancellationToken ct)
    {
        if (!_options.Enabled) return 0;   // lớp chặn 1: tắt được bằng config, mặc định TẮT

        var retentionDays = _options.RetentionDays > 0 ? _options.RetentionDays : 90;
        var batch = _options.BatchSize > 0 ? _options.BatchSize : 200;
        var cutoff = DateTime.UtcNow.AddDays(-retentionDays);

        using var scope = _scopeFactory.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<InterviewDbContext>();
        var storage = scope.ServiceProvider.GetRequiredService<IStorageService>();

        // Lớp chặn 2 — điều kiện xoá, KHÔNG được nới: ảnh chưa quá hạn giữ thì tuyệt đối không chạm.
        var rows = await db.PracticeFaceImages
            .Where(x => x.CapturedAt < cutoff)
            .OrderBy(x => x.CapturedAt)               // cũ nhất trước — dọn đều, không bỏ sót đuôi
            .Take(batch)                              // lớp chặn 3: trần mỗi vòng
            .ToListAsync(ct);

        if (rows.Count == 0) return 0;

        // Xoá object S3 TRƯỚC. Object nào xoá lỗi thì GIỮ NGUYÊN dòng sổ của nó (không đưa vào
        // `purgedIds`) → vòng sau thử lại. Thà retry mãi còn hơn mất dấu vết.
        var purgedIds = new List<Guid>(rows.Count);
        foreach (var row in rows)
        {
            try
            {
                await storage.DeleteObjectAsync(row.StorageKey, ct);
            }
            catch (Exception ex)
            {
                _logger.LogWarning(ex,
                    "PracticeFaceImagePurger: xoá object S3 '{Key}' thất bại — GIỮ dòng sổ để vòng sau thử lại",
                    row.StorageKey);
                continue;
            }
            purgedIds.Add(row.Id);
        }

        if (purgedIds.Count == 0) return 0;

        var deleted = await db.PracticeFaceImages
            .Where(x => purgedIds.Contains(x.Id))
            .ExecuteDeleteAsync(ct);

        _logger.LogInformation(
            "PracticeFaceImagePurger: đã xoá {Deleted} ảnh webcam chụp trước {Cutoff:u} (giữ {Days} ngày, DATA-3)",
            deleted, cutoff, retentionDays);

        return deleted;
    }
}
