using Isas.CampaignService.Models;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Options;

namespace Isas.CampaignService.Services
{
    /// <summary>
    /// MON1-B2/B3 — phép đo ĐỘC LẬP phía server: giám sát khuôn mặt có bị ĐỨT trong buổi thi không.
    ///
    /// Đọc <see cref="FaceImage"/> kind=Live (server ghi mỗi ~30s khi lượt kiểm mặt đến nơi — client
    /// không xoá/không làm giả được dòng đó). Hai luật, đều ghi cờ <c>session_flags</c>
    /// (<c>signal_type = "monitoring_gap"</c>, <c>source = 'Server'</c>):
    ///
    ///  • <b>LUẬT 1 (B2)</b> — khoảng trống GIỮA hai lượt kiểm: mỗi cặp ảnh Live LIÊN TIẾP cách nhau
    ///    quá <see cref="MonitoringGapSettings.GapThresholdSeconds"/>. Chỉ xét khoảng giữa (cặp liên
    ///    tiếp) ⇒ im lặng ở đuôi buổi (kiểm mặt dừng khi nộp bài, mà <c>interview_status</c> lật
    ///    Completed trễ vì event qua RabbitMQ) tự nhiên không bị gắn — không có ảnh "sau".
    ///
    ///  • <b>LUẬT 2 (B3)</b> — KHÔNG có lượt kiểm nào: buổi đã kết thúc (membership terminal), đủ dài
    ///    (<see cref="MonitoringGapSettings.MinDurationSeconds"/>) mà <c>face_images</c> kind=Live cho
    ///    session = 0. Lấp đúng đòn B2 không bắt được: chặn endpoint ngay giây đầu ⇒ 0 ảnh ⇒ B2 (cần
    ///    HAI điểm để so) không có gì để đo. Chờ terminal để tránh độ trễ event.
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

        // LUẬT 2 — mỗi session tối đa 1 cờ loại này; marker CỐ ĐỊNH trong note (session_id đã nằm
        // trong vị ngữ hậu kiểm). Khác marker LUẬT 1 (`[gap#<ticks>]`) nên hai luật dedup độc lập.
        private const string NoShotMarker = "[monitor#none]";

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

        /// <summary>Kết quả một nhịp quét. <c>GapsDetected</c> = số khoảng trống GIỮA vượt ngưỡng
        /// (LUẬT 1); <c>NoShotSessions</c> = số buổi terminal đủ dài mà 0 ảnh (LUẬT 2);
        /// <c>FlagsWritten</c> = tổng số cờ THỰC SỰ ghi (0 ở chế độ bóng hoặc khi đã có cờ).</summary>
        public sealed record MonitoringGapScan(int GapsDetected, int NoShotSessions, int FlagsWritten);

        private async Task<MonitoringGapScan> ScanOnceAsync(CancellationToken ct)
        {
            using var scope = _scopeFactory.CreateScope();
            var db = scope.ServiceProvider.GetRequiredService<CampaignDbContext>();

            var now = DateTime.UtcNow;
            var threshold = TimeSpan.FromSeconds(
                _options.GapThresholdSeconds > 0 ? _options.GapThresholdSeconds : 90);
            var minDuration = TimeSpan.FromSeconds(
                _options.MinDurationSeconds > 0 ? _options.MinDurationSeconds : 120);
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
                return new MonitoringGapScan(0, 0, 0);

            int gaps = 0, noShot = 0, written = 0;

            // ── LUẬT 1 (B2): khoảng trống GIỮA hai lượt kiểm ─────────────────────────────────────
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

                    gaps++;

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

            // ── LUẬT 2 (B3): KHÔNG có lượt kiểm nào trong suốt buổi thi ─────────────────────────
            // Chỉ buổi ĐÃ TERMINAL (Completed/Abandoned — set ở RankingEventHandler khi event về qua
            // RabbitMQ). Chờ terminal để KHÔNG gắn oan buổi đang chạy / vừa nộp (độ trễ event, SỰ THẬT
            // ĐÃ ĐO #5). Cận trên `m.UpdatedAt >= since` dùng lại LookbackHours (buổi kết thúc quá lâu
            // thì HR đã xem / không xử lý được). DbSet có filter soft-delete (DB13).
            var terminals = await db.CampaignMemberships
                .Where(m => m.SessionId != null
                    && m.CandidateId != null
                    && m.InterviewStartedAt != null
                    && (m.InterviewStatus == InterviewProgressStatus.Completed
                        || m.InterviewStatus == InterviewProgressStatus.Abandoned)
                    && m.UpdatedAt >= since
                    && fveCampaignIds.Contains(m.CampaignId))
                .Select(m => new
                {
                    SessionId = m.SessionId!.Value,
                    m.CampaignId,
                    CandidateId = m.CandidateId!.Value,
                    StartedAt = m.InterviewStartedAt!.Value,
                    m.UpdatedAt
                })
                .ToListAsync(ct);

            foreach (var m in terminals)
            {
                // Thời lượng buổi ≈ mốc terminal (UpdatedAt bị RankingEventHandler bump khi lật
                // Completed/Abandoned) trừ mốc bắt đầu. Buổi quá ngắn ⇒ chưa tới nhịp kiểm đầu tiên,
                // 0 ảnh là bình thường.
                var duration = m.UpdatedAt - m.StartedAt;
                if (duration <= minDuration)
                    continue;

                bool hasShot = await db.FaceImages.AnyAsync(
                    f => f.SessionId == m.SessionId && f.Kind == FaceImageKind.Live, ct);
                if (hasShot)
                    continue;

                noShot++;

                var note =
                    $"Không nhận được lượt kiểm khuôn mặt nào trong suốt buổi thi "
                    + $"({FormatMinutes(duration)}). {NoShotMarker}";

                _logger.LogInformation(
                    "monitoring_gap: session {SessionId} (campaign {CampaignId}) — 0 ảnh giám sát "
                    + "trong suốt buổi thi {Minutes} phút (ngưỡng tối thiểu {MinSec}s)",
                    m.SessionId, m.CampaignId, Math.Round(duration.TotalMinutes), minDuration.TotalSeconds);

                // CHỐNG GẮN TRÙNG — mỗi session tối đa 1 cờ loại này. Hậu kiểm marker cố định.
                bool exists = await db.SessionFlags.AnyAsync(f =>
                    f.SessionId == m.SessionId
                    && f.Source == FlagSource.Server
                    && f.SignalType == "monitoring_gap"
                    && f.Note != null
                    && f.Note.Contains(NoShotMarker), ct);
                if (exists)
                {
                    _logger.LogDebug(
                        "monitoring_gap: session {SessionId} đã có cờ '0 ảnh' — bỏ qua", m.SessionId);
                    continue;
                }

                if (!_options.Enabled)
                {
                    _logger.LogInformation(
                        "monitoring_gap [chế độ bóng]: session {SessionId} — 0 ảnh giám sát, SẼ ghi cờ "
                        + "nếu bật (MonitoringGap:Enabled=true)", m.SessionId);
                    continue;
                }

                db.SessionFlags.Add(new SessionFlag
                {
                    Id = Guid.NewGuid(),
                    SessionId = m.SessionId,
                    CampaignId = m.CampaignId,
                    CandidateId = m.CandidateId,
                    SignalType = "monitoring_gap",
                    Source = FlagSource.Server,
                    Note = note,
                    DetectedAt = now
                });
                written++;
            }

            if (written > 0)
            {
                await db.SaveChangesAsync(ct);
                _logger.LogWarning(
                    "MonitoringGapSweeper: ghi {Written} cờ monitoring_gap source=Server "
                    + "({Gaps} khoảng trống giữa, {NoShot} buổi 0 ảnh)",
                    written, gaps, noShot);
            }

            return new MonitoringGapScan(gaps, noShot, written);
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

        // LUẬT 2 — "suốt buổi thi (X phút)": thang phút cho toàn buổi. Làm tròn, sàn 1 (buổi qua được
        // MinDurationSeconds mặc định 120 luôn ≥ 2 phút; sàn 1 chỉ phòng khi hạ config).
        private static string FormatMinutes(TimeSpan g)
            => $"{Math.Max(1, (int)Math.Round(g.TotalMinutes))} phút";
    }
}
