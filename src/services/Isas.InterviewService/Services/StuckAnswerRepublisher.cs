using Isas.InterviewService.ApplicationDbContext;
using Isas.InterviewService.DTOs;
using Isas.InterviewService.Entities;
using Isas.InterviewService.Enums;
using Isas.InterviewService.Models;
using Isas.InterviewService.Services.Interfaces;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Options;

namespace Isas.InterviewService.Services;

public class StuckAnswerRepublisher : BackgroundService
{
    private static readonly TimeSpan ScanInterval = TimeSpan.FromMinutes(2);

    // Uploaded mà chưa publish lần nào (LastScoringPublishedAt == null) quá ngưỡng này
    // = publish hụt lúc upload -> đẩy lại sớm. Đo theo CreatedAt để chừa cửa sổ
    // upload đang dở (request còn chạy, status chưa kịp thành Scoring).
    private static readonly TimeSpan PublishFailedThreshold = TimeSpan.FromMinutes(2);

    // Đã publish nhưng quá lâu không thấy callback = worker mất tích (crash/mất message)
    // -> đẩy lại. Để dài để không đua với worker đang chấm chậm. Đo theo LastScoringPublishedAt.
    private static readonly TimeSpan ScoringLostThreshold = TimeSpan.FromMinutes(15);

    private readonly IServiceScopeFactory _scopeFactory;
    private readonly IScoringJobPublisher _publisher;  // singleton, inject thẳng được
    private readonly RepublisherSettings _options;     // DB29 — trần batch mỗi vòng
    // Kill-switch đáp án mẫu PHẢI đọc ở cả hai đường publish, nếu không tắt cờ mà answer đi đường cứu
    // hộ vẫn được chấm kèm đáp án ⇒ "đã tắt" mà hành vi chỉ đổi một nửa.
    private readonly ScoringOptions _scoring;
    private readonly ILogger<StuckAnswerRepublisher> _logger;

    public StuckAnswerRepublisher(
        IServiceScopeFactory scopeFactory,
        IScoringJobPublisher publisher,
        IOptions<RepublisherSettings> options,
        IOptions<ScoringOptions> scoring,
        ILogger<StuckAnswerRepublisher> logger)
    {
        _scopeFactory = scopeFactory;
        _publisher = publisher;
        _options = options.Value;
        _scoring = scoring.Value;
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
                _logger.LogError(ex, "Lỗi khi quét answer kẹt");
            }

            await Task.Delay(ScanInterval, ct);
        }
    }

    private async Task ScanOnceAsync(CancellationToken ct)
    {
        // BackgroundService là singleton -> phải tạo scope riêng cho DbContext (scoped).
        using var scope = _scopeFactory.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<InterviewDbContext>();

        var now = DateTime.UtcNow;
        var publishGrace = now - PublishFailedThreshold;  // cho upload kịp publish
        var scoringCutoff = now - ScoringLostThreshold;   // mốc coi là worker mất tích

        // Answer cần (re)publish: thuộc session InProgress/Scoring, đã có audio, và:
        //  - publish hụt: chưa publish lần nào (null) và upload đã quá grace, HOẶC
        //  - kẹt thật: đã publish nhưng quá lâu (LastScoringPublishedAt < cutoff).
        var stuck = await db.PracticeAnswers
            .Where(a => a.AudioObjectKey != null
                        && (a.Session.Status == SessionStatus.InProgress
                            || a.Session.Status == SessionStatus.Scoring)
                        && (a.Status == AnswerStatus.Uploaded
                            || a.Status == AnswerStatus.Scoring)
                        && (
                            (a.LastScoringPublishedAt == null && a.CreatedAt < publishGrace)
                            || (a.LastScoringPublishedAt != null && a.LastScoringPublishedAt < scoringCutoff)
                        ))
            .OrderBy(a => a.CreatedAt)   // DB29: cũ trước — batch tất định, answer kẹt lâu nhất được cứu trước
            .Select(a => new
            {
                a.Id,
                a.SessionId,
                a.QuestionId,
                a.AudioObjectKey,
                a.Transcript,   // adaptive: nếu đã transcribe đồng bộ → re-publish cũng mang theo (worker bỏ Whisper)
                // Con dấu engine đi CẶP với transcript. Thiếu ở đây thì answer nào phải cứu bằng
                // republisher sẽ mất lai lịch bản chép, trong khi answer chấm trơn tru vẫn có —
                // lệch âm thầm, đúng loại hỏng mà F11 đã dính ở CHÍNH projection này.
                a.TranscriptEngine,
                // F11 — chỉ số cách nói đã đo (đường thích ứng lưu từ /decide-next). Republisher KHÔNG
                // gọi lại AIService nên phải lấy bản đã lưu; thiếu ở đây là answer nào phải cứu bằng
                // republisher thì mất chỉ số, trong khi answer chấm trơn tru lại có — lệch âm thầm.
                a.SpeechRateWpm,
                a.FillerCount,
                a.PauseCount,
                a.LongestPauseSec,
                a.SilenceRatio,
                a.FillerBreakdown,
                a.AudioSec,
                a.SpeechSec,
                a.WordCount,
                a.FillerPer100Words,
                a.MetricsVersion,
                CampaignId = a.Session.CampaignId,
                // Phiên bản rubric buổi thi đã GHIM — PHẢI có trong projection. Thiếu nó thì answer
                // nào phải cứu bằng republisher sẽ được chấm bằng bộ tiêu chí MỚI NHẤT, trong khi
                // answer chạy trơn tru được chấm bằng bộ đã ghim ⇒ cùng một answer sinh hai
                // rubric_version khác nhau ⇒ attemptsForVersion không bao giờ đủ N ⇒ answer kẹt
                // Scoring vĩnh viễn. Đúng chỗ F11 và đáp án mẫu đã dính.
                CampaignRubricVersion = a.Session.CampaignRubricVersion,
                CandidateId = a.Session.CandidateId,   // BC16: resolve rubric riêng B2C
                JobCategory = a.Session.JobCategory,
                Language = a.Session.Language,
                QuestionContent = a.Question.Content,
                // Nhãn tiêu chí của câu hỏi — PHẢI có trong projection, nếu không answer nào phải cứu
                // bằng republisher sẽ được chấm theo luật KHÁC answer chạy trơn tru (ở đây là chấm đủ
                // rubric thay vì đúng phạm vi), lệch âm thầm. Đúng chỗ F11 đã dính.
                QuestionTargetCriterionIds = a.Question.TargetCriterionIds,
                // Đáp án mẫu HR soạn — cùng lý do với hai field trên: thiếu ở projection thì answer nào
                // phải cứu bằng republisher sẽ được chấm KHÔNG có đáp án mẫu, trong khi answer chạy
                // trơn tru thì có. Hai thước đo trong cùng một chiến dịch, mà điểm vẫn xếp chung bảng.
                QuestionSampleAnswer = a.Question.SampleAnswer
            })
            .Take(_options.BatchSize > 0 ? _options.BatchSize : 200)   // DB29: chặn nạp cả tồn đọng 1 lần
            .ToListAsync(ct);

        if (stuck.Count == 0) return;

        _logger.LogWarning("Phát hiện {Count} answer kẹt Uploaded, đang re-publish", stuck.Count);

        // DB29 — cache tiêu chí theo "chủ rubric", KHÔNG theo answer. Mọi answer cùng campaign (B2B) hoặc
        // cùng (candidate, nghề) (B2C) dùng CHUNG một bộ tiêu chí, nên tra 1 lần/nhóm thay vì 1 lần/answer:
        // trước đây mỗi answer tốn 3 query (resolve owner + nạp criteria + ExecuteUpdate) ⇒ 3N+1 mỗi 2 phút,
        // đúng lúc broker vừa hồi phục và tồn đọng đang lớn nhất.
        var criteriaCache = new Dictionary<RubricScopeKey, List<RubricCriterion>>();

        foreach (var a in stuck)
        {
            var key = a.CampaignId is Guid cid
                // B2B: tiêu chí phụ thuộc campaign + PHIÊN BẢN buổi thi đã ghim (hai buổi cùng campaign
                // ghim hai phiên bản khác nhau KHÔNG được dùng chung entry cache).
                ? new RubricScopeKey(cid, null, null, CampaignRubricVersion: a.CampaignRubricVersion)
                : new RubricScopeKey(null, a.CandidateId, a.JobCategory, a.Language);   // B2C: theo (candidate, nghề, language)

            if (!criteriaCache.TryGetValue(key, out var criteria))
            {
                criteria = await RubricCriteriaLoader.LoadAsync(db, key, ct);
                criteriaCache[key] = criteria;
            }

            if (criteria.Count == 0)
            {
                _logger.LogWarning(
                    "Không có tiêu chí active (campaign={CampaignId}, nghề={JobCategory}), bỏ qua answer {AnswerId}",
                    a.CampaignId, a.JobCategory, a.Id);
                continue;
            }

            // Cùng luật phạm vi chấm với đường publish lúc upload (AnswerService) — dùng CHUNG helper
            // để hai đường không thể trôi khỏi nhau.
            var scopedCriteria = ScoringScopeFilter.Apply(
                criteria, a.QuestionTargetCriterionIds, _logger, a.Id);

            var job = new ScoringJob
            {
                AnswerId = a.Id,
                SessionId = a.SessionId,
                QuestionId = a.QuestionId,
                AudioObjectKey = a.AudioObjectKey!,
                QuestionContent = a.QuestionContent,
                SampleAnswer = _scoring.UseSampleAnswer ? a.QuestionSampleAnswer : null,
                JobCategory = a.JobCategory.ToString(),
                Language = a.Language,
                RubricVersion = criteria[0].Version,
                Criteria = ScoringCriteriaBuilder.Build(scopedCriteria),   // E9: kèm levels (+ anchors)
                Transcript = a.Transcript,  // adaptive: có transcript đồng bộ → worker bỏ Whisper
                TranscriptEngine = a.TranscriptEngine,   // đi cặp: worker bỏ Whisper thì không tự biết engine
                // F11 — chỉ số đã đo đi kèm; null (chưa từng đo) → worker tự transcribe rồi tự đo.
                // Vá 2026-07-19: PHẢI truyền đủ 4 cột audio/speech/word/filler-per-100, nếu không
                // prompt chấm nhận 0 giây audio dưới nhãn "số liệu thật".
                DeliveryMetrics = DeliveryMetricsMapper.Read(
                    a.SpeechRateWpm, a.FillerCount, a.PauseCount,
                    a.LongestPauseSec, a.SilenceRatio, a.FillerBreakdown,
                    a.AudioSec, a.SpeechSec, a.WordCount, a.FillerPer100Words,
                    a.MetricsVersion)
            };

            try
            {
                await _publisher.PublishAsync(job, ct);

                // Đẩy lại OK -> Scoring + dời mốc publish sang now, để vòng quét sau
                // không nhặt lại trong vòng ScoringLostThreshold. ExecuteUpdate vì
                // ở đây dùng projection (không track entity).
                await db.PracticeAnswers
                    .Where(x => x.Id == a.Id)
                    .ExecuteUpdateAsync(s => s
                        .SetProperty(x => x.Status, AnswerStatus.Scoring)
                        .SetProperty(x => x.LastScoringPublishedAt, now), ct);

                _logger.LogInformation("Re-published answer {AnswerId}", a.Id);
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Re-publish thất bại answer {AnswerId}, để vòng sau", a.Id);
            }
        }
    }

}
