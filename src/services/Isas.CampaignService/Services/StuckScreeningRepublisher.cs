using Isas.CampaignService.Models;
using Microsoft.EntityFrameworkCore;

namespace Isas.CampaignService.Services
{
    /// <summary>
    /// C15 — Quét CV sàng kẹt mỗi 2 phút, đẩy lại <c>cv_screening_queue</c> (mẫu
    /// InterviewService.StuckAnswerRepublisher). 2 loại kẹt:
    ///  • <c>Filtered</c> + <c>last_screening_published_at = null</c> quá 2 phút → publish HỤT lúc sàng
    ///    (broker down khi <see cref="CvScreeningService.PublishScreeningJobsAsync"/>) → đẩy lại.
    ///  • <c>Analyzing</c> + <c>last_screening_published_at</c> quá 15 phút không callback → worker mất tích.
    /// Đẩy lại OK → set <c>Analyzing</c> + <c>last_screening_published_at = now</c> (chống nhặt lại ngay).
    /// <c>Analyzed</c>/<c>Rejected</c>/<c>Invited</c>/<c>AnalysisFailed</c> KHÔNG bị nhặt (terminal / chờ HR retry).
    /// Chỉ PUBLISH (không consume) — nhẹ hơn <see cref="SessionScoredConsumer"/>.
    /// </summary>
    public class StuckScreeningRepublisher : BackgroundService
    {
        private static readonly TimeSpan ScanInterval = TimeSpan.FromMinutes(2);

        // Filtered chưa publish lần nào (marker null) quá ngưỡng này = publish hụt lúc sàng → đẩy lại.
        // Đo theo CreatedAt để chừa cửa sổ request sàng đang chạy dở (candidate vừa tạo, chưa kịp publish).
        private static readonly TimeSpan PublishFailedThreshold = TimeSpan.FromMinutes(2);

        // Đã publish (Analyzing) nhưng quá lâu không callback = worker mất tích → đẩy lại.
        // Để dài để không đua với worker đang chấm chậm. Đo theo LastScreeningPublishedAt.
        private static readonly TimeSpan AnalyzingLostThreshold = TimeSpan.FromMinutes(15);

        // TRẦN BỎ CUỘC — vòng lặp này KHÔNG có điểm dừng nếu không có nó.
        //
        // Đo được 2026-08-02: `cv_screening_queue` tồn **713 message của đúng 8 ứng viên**. Consumer
        // phía AIService chưa từng tồn tại, nên mỗi ứng viên bị đẩy lại 1 lần/15' suốt ~22 tiếng
        // (96 lần/ngày) và hàng đợi lớn mãi mà KHÔNG có gì báo — không alert nào đọc `list_queues`.
        // Mỗi bản nhân đôi là một lượt Gemini nếu consumer bật lên.
        //
        // Neo theo `CreatedAt` chứ KHÔNG theo `LastScreeningPublishedAt`: mốc sau bị chính vòng lặp
        // này dời về `now` mỗi lần đẩy, nên lấy nó làm trần thì trần không bao giờ tới.
        //
        // Quá trần → `AnalysisFailed` + log Error. Cố ý biến một rò rỉ vô hình thành một trạng thái
        // HR NHÌN THẤY: ⚠ hiện KHÔNG có endpoint nào cho HR retry `AnalysisFailed` (chỉ callback
        // `cv-result` đến muộn mới gỡ được — `CvScreeningService.cs:139`), nên để mặc định rộng tay
        // và chỉnh được bằng config.
        private static readonly TimeSpan DefaultGiveUpAfter = TimeSpan.FromHours(6);

        private readonly IServiceScopeFactory _scopeFactory;
        private readonly ICvScreeningPublisher _publisher;   // singleton — inject thẳng được
        private readonly IConfiguration _config;
        private readonly ILogger<StuckScreeningRepublisher> _logger;

        public StuckScreeningRepublisher(
            IServiceScopeFactory scopeFactory,
            ICvScreeningPublisher publisher,
            IConfiguration config,
            ILogger<StuckScreeningRepublisher> logger)
        {
            _scopeFactory = scopeFactory;
            _publisher = publisher;
            _config = config;
            _logger = logger;
        }

        protected override async Task ExecuteAsync(CancellationToken ct)
        {
            // Chờ 1 nhịp trước khi quét lần đầu, để app khởi động xong.
            await Task.Delay(TimeSpan.FromSeconds(30), ct);

            while (!ct.IsCancellationRequested)
            {
                try
                {
                    await ScanOnceAsync(ct);
                }
                catch (Exception ex)
                {
                    // Không để 1 vòng lỗi giết cả background service.
                    _logger.LogError(ex, "Lỗi khi quét CV sàng kẹt");
                }

                await Task.Delay(ScanInterval, ct);
            }
        }

        private async Task ScanOnceAsync(CancellationToken ct)
        {
            // BackgroundService là singleton → phải tạo scope riêng cho DbContext (scoped).
            using var scope = _scopeFactory.CreateScope();
            var db = scope.ServiceProvider.GetRequiredService<CampaignDbContext>();

            var now = DateTime.UtcNow;
            var publishGrace = now - PublishFailedThreshold;    // cho request sàng kịp publish
            var analyzingCutoff = now - AnalyzingLostThreshold; // mốc coi worker mất tích

            // Ứng viên cần (re)publish (Campaign nav có query filter DeletedAt==null → bỏ campaign đã xoá):
            //  - publish hụt: Filtered, chưa publish (null), tạo đã quá grace; HOẶC
            //  - kẹt thật: Analyzing, đã publish nhưng quá lâu (< cutoff).
            var stuck = await db.CvSubmissions
                .Where(c =>
                    (c.Status == CvSubmissionStatus.Filtered
                        && c.LastScreeningPublishedAt == null
                        && c.CreatedAt < publishGrace)
                    || (c.Status == CvSubmissionStatus.Analyzing
                        && c.LastScreeningPublishedAt != null
                        && c.LastScreeningPublishedAt < analyzingCutoff))
                .Select(c => new
                {
                    c.Id,
                    c.CampaignId,
                    c.CvParsedText,
                    c.CreatedAt,
                    Domain = c.Campaign.Domain,
                    JdText = c.Campaign.JDText
                })
                .ToListAsync(ct);

            if (stuck.Count == 0) return;

            _logger.LogWarning("Phát hiện {Count} CV sàng kẹt, đang re-publish", stuck.Count);

            var callbackBase = _config["Internal:CallbackBase"] ?? "http://localhost:8080";
            var criteriaCache = new Dictionary<Guid, List<CvScreeningCriterion>>();

            // Trần bỏ cuộc: 0 hoặc âm = TẮT trần (giữ hành vi đẩy lại vô hạn — chỉ dùng khi cố ý).
            var giveUpAfter = int.TryParse(_config["Screening:GiveUpAfterHours"], out var h)
                ? TimeSpan.FromHours(h)
                : DefaultGiveUpAfter;
            var giveUpCutoff = giveUpAfter > TimeSpan.Zero ? now - giveUpAfter : (DateTime?)null;

            foreach (var c in stuck)
            {
                // Quá trần → thôi đẩy lại, chuyển AnalysisFailed để HR NHÌN THẤY thay vì rò rỉ im lặng.
                // Đặt TRƯỚC mọi thứ khác (kể cả nạp criteria) — đã bỏ cuộc thì không tốn thêm query nào.
                if (giveUpCutoff is DateTime cutoff && c.CreatedAt < cutoff)
                {
                    await db.CvSubmissions
                        .Where(x => x.Id == c.Id)
                        .ExecuteUpdateAsync(s => s
                            .SetProperty(x => x.Status, CvSubmissionStatus.AnalysisFailed)
                            .SetProperty(x => x.RejectReason,
                                $"Sàng CV không hoàn tất sau {giveUpAfter.TotalHours:0.#} giờ — worker sàng CV không phản hồi")
                            .SetProperty(x => x.UpdatedAt, now), ct);

                    _logger.LogError(
                        "Bỏ cuộc sàng CV candidate {CandidateId} (campaign {CampaignId}): quá {Hours} giờ "
                        + "không có callback → AnalysisFailed. Kiểm tra consumer cv_screening_queue.",
                        c.Id, c.CampaignId, giveUpAfter.TotalHours);
                    continue;
                }

                // TÁI DÙNG campaign_criteria làm rubric gửi kèm job (cache theo campaign — N nhỏ do max_candidates).
                if (!criteriaCache.TryGetValue(c.CampaignId, out var criteria))
                {
                    criteria = await db.CampaignCriteria
                        .Where(cr => cr.CampaignId == c.CampaignId)
                        .OrderBy(cr => cr.OrderNo)
                        .Select(cr => new CvScreeningCriterion(cr.Id, cr.Name, cr.Description, cr.MaxScore))
                        .ToListAsync(ct);
                    criteriaCache[c.CampaignId] = criteria;
                }

                if (criteria.Count == 0)
                {
                    _logger.LogWarning(
                        "Không có campaign_criteria (campaign={CampaignId}), bỏ qua candidate {CandidateId}",
                        c.CampaignId, c.Id);
                    continue;
                }

                try
                {
                    await _publisher.PublishAsync(new CvScreeningJob(
                        c.Id,
                        c.CvParsedText ?? string.Empty,
                        c.Domain,
                        c.JdText,
                        criteria,
                        callbackBase), ct);

                    // Đẩy lại OK → Analyzing + dời mốc publish sang now, để vòng sau không nhặt lại
                    // trong AnalyzingLostThreshold. ExecuteUpdate vì đang dùng projection (không track entity).
                    await db.CvSubmissions
                        .Where(x => x.Id == c.Id)
                        .ExecuteUpdateAsync(s => s
                            .SetProperty(x => x.Status, CvSubmissionStatus.Analyzing)
                            .SetProperty(x => x.LastScreeningPublishedAt, now)
                            .SetProperty(x => x.UpdatedAt, now), ct);

                    _logger.LogInformation("Re-published CV sàng candidate {CandidateId}", c.Id);
                }
                catch (Exception ex)
                {
                    _logger.LogError(ex, "Re-publish CV sàng thất bại candidate {CandidateId}, để vòng sau", c.Id);
                }
            }
        }
    }
}
