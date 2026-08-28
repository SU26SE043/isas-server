using Isas.CampaignService.Models;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Options;

namespace Isas.CampaignService.Services
{
    /// <summary>
    /// MON1-B2 — phép đo ĐỘC LẬP phía server: giám sát khuôn mặt có bị ĐỨT giữa buổi thi không.
    ///
    /// Đọc <see cref="FaceImage"/> kind=Live (server ghi mỗi ~30s khi lượt kiểm mặt đến nơi — client
    /// không xoá/không làm giả được dòng đó). Với mỗi cặp ảnh LIÊN TIẾP trong một buổi, nếu khoảng
    /// cách <c>captured_at</c> vượt <see cref="MonitoringGapSettings.GapThresholdSeconds"/> ⇒ ghi 1 cờ
    /// <c>session_flags</c> (<c>signal_type = "monitoring_gap"</c>, <c>source = 'Server'</c>).
    ///
    /// KHÔNG giải quyết: người ngồi cạnh nhắc bài, điện thoại thứ hai, đọc màn hình khác — không cờ
    /// nào đụng tới. Cần chia sẻ màn hình / multi_voice, thuộc đợt khác.
    ///
    /// Mẫu chu kỳ/scope/log/cận-trên: <see cref="StuckScreeningRepublisher"/>, <see cref="FaceImagePurger"/>.
    /// <c>ScanOnceAsync</c> private → test gọi qua reflection (idiom repo).
    /// </summary>
    public class MonitoringGapSweeper : BackgroundService
    {
        // Nhịp kiểm mặt phía FE = 30s (dùng trong câu mô tả phép đo cho HR).
        private const int NormalCadenceSeconds = 30;

        private readonly IServiceScopeFactory _scopeFactory;
        private readonly MonitoringGapSettings _options;
        private readonly ILogger<MonitoringGapSweeper> _logger;

        public MonitoringGapSweeper(
            IServiceScopeFactory scopeFactory,
            IOptions<MonitoringGapSettings> options,
            ILogger<MonitoringGapSweeper> logger)
        {
            _scopeFactory = scopeFactory;
            _options = options.Value;
            _logger = logger;
        }

        protected override async Task ExecuteAsync(CancellationToken ct)
        {
            // Chờ 1 nhịp để app khởi động xong.
            await Task.Delay(TimeSpan.FromSeconds(30), ct);

            var interval = TimeSpan.FromSeconds(
                _options.ScanIntervalSeconds > 0 ? _options.ScanIntervalSeconds : 120);

            while (!ct.IsCancellationRequested)
            {
                try
                {
                    await ScanOnceAsync(ct);
                }
                catch (Exception ex)
                {
                    // Không để 1 vòng lỗi giết cả background service.
                    _logger.LogError(ex, "Lỗi khi quét khoảng trống giám sát khuôn mặt (monitoring_gap)");
                }

                await Task.Delay(interval, ct);
            }
        }

        /// <summary>Kết quả một nhịp quét — <c>GapsDetected</c> = số khoảng trống VƯỢT ngưỡng thấy
        /// được; <c>FlagsWritten</c> = số cờ thực sự ghi (0 ở chế độ bóng hoặc khi đã có cờ).</summary>
        public sealed record MonitoringGapScan(int GapsDetected, int FlagsWritten);

        private async Task<MonitoringGapScan> ScanOnceAsync(CancellationToken ct)
        {
            using var scope = _scopeFactory.CreateScope();
            var db = scope.ServiceProvider.GetRequiredService<CampaignDbContext>();

            var now = DateTime.UtcNow;
            var threshold = TimeSpan.FromSeconds(
                _options.GapThresholdSeconds > 0 ? _options.GapThresholdSeconds : 90);
            var lookback = TimeSpan.FromHours(
                _options.LookbackHours > 0 ? _options.LookbackHours : 48);
            var since = now - lookback;

            // GUARD SỐ MỘT — chỉ campaign BẬT face_verify. Campaign chỉ bật anti_cheat (không face_verify)
            // sinh 0 ảnh Live ⇒ quét chúng là gắn cờ 100% người vô tội. Qua DbSet có filter soft-delete
            // (DB13) ⇒ campaign đã xoá tự loại (buổi thi của nó cũng moot).
            var fveCampaignIds = await db.Campaigns
                .Where(c => c.FaceVerifyEnabled)
                .Select(c => c.Id)
                .ToListAsync(ct);
            if (fveCampaignIds.Count == 0)
                return new MonitoringGapScan(0, 0);

            // Ảnh giám sát (Live) trong cửa sổ nhìn lại, thuộc campaign face_verify.
            // FaceImage cố ý KHÔNG có nav Campaign (BK25) ⇒ lọc bằng danh sách id.
            var shots = await db.FaceImages
                .Where(f => f.Kind == FaceImageKind.Live
                    && f.SessionId != null
                    && f.CapturedAt >= since
                    && fveCampaignIds.Contains(f.CampaignId))
                .Select(f => new
                {
                    SessionId = f.SessionId!.Value,
                    f.CampaignId,
                    f.CandidateId,
                    f.CapturedAt
                })
                .ToListAsync(ct);

            if (shots.Count == 0)
                return new MonitoringGapScan(0, 0);

            int detected = 0, written = 0;

            foreach (var session in shots.GroupBy(s => s.SessionId))
            {
                var ordered = session.OrderBy(s => s.CapturedAt).ToList();

                for (int i = 1; i < ordered.Count; i++)
                {
                    var prev = ordered[i - 1];
                    var cur = ordered[i];
                    var gap = cur.CapturedAt - prev.CapturedAt;
                    if (gap <= threshold)
                        continue;

                    detected++;

                    // Mã hoá mốc bắt đầu khoảng trống vào note theo định dạng ỔN ĐỊNH (Ticks không phụ
                    // thuộc DateTimeKind, không đổi giữa các lần quét, không ký tự đặc biệt của LIKE).
                    // (session_id, prev.captured_at) định danh duy nhất một khoảng trống.
                    var marker = $"[gap#{prev.CapturedAt.Ticks}]";
                    var note =
                        $"Không nhận được lượt kiểm khuôn mặt nào trong {FormatDuration(gap)} " +
                        $"(nhịp bình thường {NormalCadenceSeconds} giây). {marker}";

                    _logger.LogInformation(
                        "monitoring_gap: session {SessionId} (campaign {CampaignId}) — khoảng trống {Seconds:0}s "
                        + "giữa 2 lượt kiểm mặt (ngưỡng {Threshold}s)",
                        prev.SessionId, prev.CampaignId, gap.TotalSeconds, threshold.TotalSeconds);

                    // CHỐNG GẮN TRÙNG — session_flags KHÔNG có UNIQUE, sweeper chạy mỗi vài phút. Hậu kiểm
                    // đã có cờ source='Server' cùng session cùng mốc gap chưa. AnyAsync đi qua query filter
                    // soft-delete (khớp nhánh write); campaign đã xoá thì cũng không nằm trong fveCampaignIds
                    // nên buổi đó không được quét — nhất quán.
                    bool exists = await db.SessionFlags.AnyAsync(f =>
                        f.SessionId == prev.SessionId
                        && f.Source == FlagSource.Server
                        && f.SignalType == "monitoring_gap"
                        && f.Note != null
                        && f.Note.Contains(marker), ct);
                    if (exists)
                    {
                        _logger.LogDebug(
                            "monitoring_gap: session {SessionId} mốc {Ticks} đã có cờ Server — bỏ qua",
                            prev.SessionId, prev.CapturedAt.Ticks);
                        continue;
                    }

                    // CHẾ ĐỘ BÓNG — tính và log xong nhưng KHÔNG ghi.
                    if (!_options.Enabled)
                    {
                        _logger.LogInformation(
                            "monitoring_gap [chế độ bóng]: session {SessionId} — SẼ ghi cờ nếu bật "
                            + "(MonitoringGap:Enabled=true)", prev.SessionId);
                        continue;
                    }

                    db.SessionFlags.Add(new SessionFlag
                    {
                        Id = Guid.NewGuid(),
                        SessionId = prev.SessionId,
                        CampaignId = prev.CampaignId,
                        CandidateId = prev.CandidateId,
                        SignalType = "monitoring_gap",
                        Source = FlagSource.Server,
                        Note = note,
                        DetectedAt = now
                    });
                    written++;
                }
            }

            if (written > 0)
            {
                await db.SaveChangesAsync(ct);
                _logger.LogWarning(
                    "MonitoringGapSweeper: ghi {Written}/{Detected} cờ monitoring_gap (source=Server)",
                    written, detected);
            }

            return new MonitoringGapScan(detected, written);
        }

        // "4 phút 12 giây" · "2 phút" · "45 giây" · "1 giờ 5 phút". Mô tả PHÉP ĐO cho HR — CAMP-12:
        // không viết "nghi gian lận" / "ứng viên đã rời đi"; ta chỉ biết KHÔNG QUAN SÁT ĐƯỢC.
        private static string FormatDuration(TimeSpan g)
        {
            int total = (int)Math.Round(g.TotalSeconds);
            int h = total / 3600, m = total % 3600 / 60, s = total % 60;

            var parts = new List<string>(3);
            if (h > 0) parts.Add($"{h} giờ");
            if (m > 0) parts.Add($"{m} phút");
            if (s > 0 || parts.Count == 0) parts.Add($"{s} giây");
            return string.Join(" ", parts);
        }
    }
}
