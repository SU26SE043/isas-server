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
    // ⚠ 2026-08-20 — ba mốc dưới đây TỪNG là hằng `static readonly` ngay tại đây. Chúng đã dọn sang
    // `RepublisherSettings` (đọc từ cấu hình `Republisher:*`), vì đo prod cho thấy `ScoringLostMinutes`
    // 15' là thứ QUYẾT ĐỊNH độ trễ thấy điểm của người dùng (p90 = 572,9s), mà đổi nó lại phải build
    // lại image. **Lý do CHỌN từng con số nằm ở `RepublisherSettings` — đọc ở đó, đừng đoán ở đây.**
    // Chốt một lần lúc dựng service: `IOptions<T>` không nóng lại, đọc lại mỗi vòng chỉ tạo ảo giác.
    private readonly TimeSpan _scanInterval;
    private readonly TimeSpan _publishFailedThreshold;   // publish hụt lúc upload (đo theo CreatedAt)
    private readonly TimeSpan _scoringLostThreshold;     // worker mất tích (đo theo LastScoringPublishedAt)

    private readonly IServiceScopeFactory _scopeFactory;
    private readonly IScoringJobPublisher _publisher;  // singleton, inject thẳng được
    private readonly RepublisherSettings _options;     // DB29 — trần batch mỗi vòng
    // E10 — N attempt + trần bỏ cuộc. Đồng thời là kill-switch đáp án mẫu: cờ PHẢI đọc ở cả hai
    // đường publish, nếu không tắt cờ mà answer đi đường cứu hộ vẫn được chấm kèm đáp án ⇒ "đã
    // tắt" mà hành vi chỉ đổi một nửa.
    private readonly ScoringOptions _scoring;
    private readonly ILogger<StuckAnswerRepublisher> _logger;

    public StuckAnswerRepublisher(
        IServiceScopeFactory scopeFactory,
        IScoringJobPublisher publisher,
        IOptions<RepublisherSettings> options,
        IOptions<ScoringOptions> scoringOptions,
        ILogger<StuckAnswerRepublisher> logger)
    {
        _scopeFactory = scopeFactory;
        _publisher = publisher;
        _options = options.Value;
        _scoring = scoringOptions.Value;
        _logger = logger;

        // Giá trị ≤ 0 (env khai nhầm / khai rỗng) KHÔNG được phép lọt: chu kỳ 0 biến vòng quét thành
        // vòng lặp nóng, còn ngưỡng 0 thì đẩy lại MỌI answer đang chấm ở mỗi vòng ⇒ nhân đôi hoá đơn
        // Gemini một cách im lặng. Rơi về mặc định, đúng mẫu `BatchSize > 0 ? ... : 200` bên dưới.
        _scanInterval = Minutes(_options.ScanIntervalMinutes, 2);
        _publishFailedThreshold = Minutes(_options.PublishFailedMinutes, 2);
        _scoringLostThreshold = Minutes(_options.ScoringLostMinutes, 3);

        static TimeSpan Minutes(int value, int fallback)
            => TimeSpan.FromMinutes(value > 0 ? value : fallback);
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

            await Task.Delay(_scanInterval, ct);
        }
    }

    private async Task ScanOnceAsync(CancellationToken ct)
    {
        // BackgroundService là singleton -> phải tạo scope riêng cho DbContext (scoped).
        using var scope = _scopeFactory.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<InterviewDbContext>();

        var now = DateTime.UtcNow;
        var publishGrace = now - _publishFailedThreshold;  // cho upload kịp publish
        var scoringCutoff = now - _scoringLostThreshold;   // mốc coi là worker mất tích

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
                a.CreatedAt,   // E10b — mốc trần BỎ CUỘC (KHÔNG dùng LastScoringPublishedAt, xem ScoringOptions)
                CampaignId = a.Session.CampaignId,
                // Phiên bản rubric buổi thi đã GHIM — PHẢI có trong projection. Thiếu nó thì answer
                // nào phải cứu bằng republisher sẽ được chấm bằng bộ tiêu chí MỚI NHẤT, trong khi
                // answer chạy trơn tru được chấm bằng bộ đã ghim ⇒ cùng một answer sinh hai
                // rubric_version khác nhau ⇒ attemptsForVersion không bao giờ đủ N ⇒ answer kẹt
                // Scoring vĩnh viễn. Đúng chỗ F11 và đáp án mẫu đã dính.
                CampaignRubricVersion = a.Session.CampaignRubricVersion,
                // Cặp con dấu rubric B2C — cùng lý do với `CampaignRubricVersion` ngay trên: thiếu ở
                // projection thì answer nào phải cứu bằng republisher rơi về nhánh "bộ đang hiệu lực",
                // trong khi answer chấm trơn tru dùng bộ đã ghim ⇒ cùng một answer sinh hai
                // rubric_version ⇒ attemptsForVersion không bao giờ đủ N ⇒ answer kẹt Scoring vĩnh viễn.
                B2CRubricOwnerId = a.Session.B2CRubricOwnerId,
                B2CRubricVersion = a.Session.B2CRubricVersion,
                CandidateId = a.Session.CandidateId,   // BC16: resolve rubric riêng B2C
                JobCategory = a.Session.JobCategory,
                Language = a.Session.Language,
                // J5 — PHẢI có trong projection: thiếu nó thì answer nào phải cứu bằng republisher
                // sẽ chấm KHÔNG hiệu chỉnh theo cấp độ, trong khi answer chạy trơn tru (AnswerService)
                // có — cùng đúng lớp lệch mà mọi field trên đã dính (F11/đáp án mẫu/rubric ghim).
                Seniority = a.Session.Seniority,
                // E10b — hai cột quyết định "answer này cần MẤY attempt". Thiếu chúng ở projection
                // thì republisher không thể biết attempt nào còn thiếu và sẽ đẩy lại nhầm attempt.
                a.Session.SelfConsistencyN,
                a.Session.EntitlementSource,
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

        // E10b — attempt ĐÃ CÓ điểm của cả batch, nạp MỘT lần (không N+1). Republisher phải bù đúng
        // attempt còn THIẾU: trước đây nó dựng ScoringJob không set AttemptNo ⇒ nhận mặc định 1 ⇒ đẩy
        // lại attempt 1 mãi mãi. Với buổi N>1 mà một attempt chết, số attempt distinct KHÔNG BAO GIỜ
        // lên tới N ⇒ answer treo `Scoring` vĩnh viễn (sự cố 2026-08-15).
        var answerIds = stuck.Select(x => x.Id).ToList();
        var scoredAttempts = (await db.AnswerScores.AsNoTracking()
                .Where(s => answerIds.Contains(s.AnswerId))
                .Select(s => new { s.AnswerId, s.RubricVersion, s.AttemptNo })
                .Distinct()
                .ToListAsync(ct))
            .GroupBy(x => x.AnswerId)
            .ToDictionary(g => g.Key, g => g.ToList());

        // Trần bỏ cuộc: quá hạn thì THÔI đẩy (SessionAbandonSweeper chốt sổ). Không có vế này thì
        // vòng đẩy-lại là vô hạn và mỗi vòng đốt thêm một lượt Gemini cho một answer đã hết cứu.
        var giveUpCutoff = _scoring.GiveUpAfterMinutes > 0
            ? now.AddMinutes(-_scoring.GiveUpAfterMinutes)
            : (DateTime?)null;

        // DB29 — cache tiêu chí theo "chủ rubric", KHÔNG theo answer. Mọi answer cùng campaign (B2B) hoặc
        // cùng (candidate, nghề) (B2C) dùng CHUNG một bộ tiêu chí, nên tra 1 lần/nhóm thay vì 1 lần/answer:
        // trước đây mỗi answer tốn 3 query (resolve owner + nạp criteria + ExecuteUpdate) ⇒ 3N+1 mỗi 2 phút,
        // đúng lúc broker vừa hồi phục và tồn đọng đang lớn nhất.
        var criteriaCache = new Dictionary<RubricScopeKey, List<RubricCriterion>>();

        foreach (var a in stuck)
        {
            if (giveUpCutoff is DateTime cutoff && a.CreatedAt < cutoff)
            {
                _logger.LogWarning(
                    "Answer {AnswerId} quá trần bỏ cuộc ({Minutes}') — thôi đẩy lại, chờ sweeper chốt sổ",
                    a.Id, _scoring.GiveUpAfterMinutes);
                continue;
            }

            var key = a.CampaignId is Guid cid
                // B2B: tiêu chí phụ thuộc campaign + PHIÊN BẢN buổi thi đã ghim (hai buổi cùng campaign
                // ghim hai phiên bản khác nhau KHÔNG được dùng chung entry cache).
                ? new RubricScopeKey(cid, null, null, CampaignRubricVersion: a.CampaignRubricVersion)
                // B2C: theo (candidate, nghề, language) + CON DẤU rubric buổi đã ghim (chủ + phiên bản).
                : new RubricScopeKey(null, a.CandidateId, a.JobCategory, a.Language,
                    B2COwnerId: a.B2CRubricOwnerId, B2CRubricVersion: a.B2CRubricVersion);

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

            var rubricVersion = criteria[0].Version;

            // E10b — CHỈ bù attempt còn thiếu, và bù theo ĐÚNG số attempt mà AnswerService sẽ đếm khi
            // xét "đã đủ chưa" (dùng chung ScoringAttemptPolicy để hai bên không thể lệch nhau).
            var required = ScoringAttemptPolicy.Resolve(
                a.CampaignId, a.EntitlementSource, a.SelfConsistencyN, _scoring.SelfConsistencyN);
            var done = scoredAttempts.TryGetValue(a.Id, out var rows)
                ? rows.Where(r => r.RubricVersion == rubricVersion).Select(r => r.AttemptNo).ToHashSet()
                : new HashSet<int>();
            var missing = Enumerable.Range(1, required).Where(x => !done.Contains(x)).ToList();

            if (missing.Count == 0)
            {
                // Đủ attempt mà answer vẫn `Scoring` ⇒ callback cuối cùng đã ghi điểm nhưng bước chốt
                // (median + đóng session) không chạy tới nơi. Đẩy lại thêm KHÔNG cứu được gì — sweeper
                // mới là chỗ chốt sổ. Log để hiện tượng này không vô hình.
                _logger.LogWarning(
                    "Answer {AnswerId} đã đủ {Required} attempt (rubric v{Version}) nhưng vẫn Scoring — "
                    + "không đẩy lại, chờ sweeper chốt sổ", a.Id, required, rubricVersion);
                continue;
            }

            // E9: kèm levels (+ anchors). Cờ dải mặc định đọc ở ĐÂY NỮA, không chỉ ở AnswerService:
            // một answer có thể được chấm bởi cả hai đường, và hai đường dùng thước khác nhau thì
            // median E10 gộp hai thước đo mà không có triệu chứng nào.
            //
            // Tiêu chí chấm bằng SỐ ĐO bị BỎ khỏi bộ gửi đi — ĐỌC Ở ĐÂY NỮA, không chỉ ở AnswerService:
            // một answer có thể được chấm bởi cả hai đường, hai đường lệch luật thì median E10 gộp hai
            // thước đo mà không có triệu chứng nào.
            var aiCriteria = MeasuredCriteriaSplit.ForAi(scopedCriteria, _logger, a.Id);
            var builtCriteria = ScoringCriteriaBuilder.Build(aiCriteria, _scoring.DefaultBandStyle);
            var published = 0;

            foreach (var attempt in missing)
            {
                var job = new ScoringJob
                {
                    AnswerId = a.Id,
                    SessionId = a.SessionId,
                    QuestionId = a.QuestionId,
                    AudioObjectKey = a.AudioObjectKey!,
                    QuestionContent = a.QuestionContent,
                    // Đáp án mẫu HR soạn: kill-switch `Scoring:UseSampleAnswer` phải đọc ở CẢ HAI
                    // đường publish (AnswerService lúc upload + đường cứu hộ này), nếu không tắt cờ
                    // mà answer đi đường cứu hộ vẫn chấm kèm đáp án ⇒ "đã tắt" mà hành vi chỉ đổi một nửa.
                    SampleAnswer = _scoring.UseSampleAnswer ? a.QuestionSampleAnswer : null,
                    JobCategory = a.JobCategory.ToString(),
                    Language = a.Language,
                    RubricVersion = rubricVersion,
                    Criteria = builtCriteria,
                    // E10 — giữ ĐÚNG hợp đồng của đường publish lúc upload: attempt 1 luôn temp=0
                    // (tái lập), 2..N mới dao động. Bù attempt 2 bằng temp=0 sẽ làm spread giả = 0.
                    AttemptNo = attempt,
                    Temperature = attempt == 1 ? 0d : _scoring.SelfConsistencyTemperature,
                    Transcript = a.Transcript,  // adaptive: có transcript đồng bộ → worker bỏ Whisper
                    TranscriptEngine = a.TranscriptEngine,   // đi cặp: worker bỏ Whisper thì không tự biết engine
                    // F11 — chỉ số đã đo đi kèm; null (chưa từng đo) → worker tự transcribe rồi tự đo.
                    // Vá 2026-07-19: PHẢI truyền đủ 4 cột audio/speech/word/filler-per-100, nếu không
                    // prompt chấm nhận 0 giây audio dưới nhãn "số liệu thật".
                    DeliveryMetrics = DeliveryMetricsMapper.Read(
                        a.SpeechRateWpm, a.FillerCount, a.PauseCount,
                        a.LongestPauseSec, a.SilenceRatio, a.FillerBreakdown,
                        a.AudioSec, a.SpeechSec, a.WordCount, a.FillerPer100Words,
                        a.MetricsVersion),
                    // J5 — CHỈ B2C, giữ ĐÚNG van của đường publish lúc upload (AnswerService).
                    Seniority = a.CampaignId is null ? a.Seniority : null
                };

                try
                {
                    await _publisher.PublishAsync(job, ct);
                    published++;
                }
                catch (Exception ex)
                {
                    _logger.LogError(ex,
                        "Re-publish thất bại answer {AnswerId} attempt {Attempt}, để vòng sau", a.Id, attempt);
                }
            }

            // không dời mốc: vòng sau thử lại ngay, không phải chờ hết `Republisher:ScoringLostMinutes`
            if (published == 0) continue;

            try
            {
                // Đẩy lại OK -> Scoring + dời mốc publish sang now, để vòng quét sau
                // không nhặt lại trong vòng `Republisher:ScoringLostMinutes`. ExecuteUpdate vì
                // ở đây dùng projection (không track entity).
                await db.PracticeAnswers
                    .Where(x => x.Id == a.Id)
                    .ExecuteUpdateAsync(s => s
                        .SetProperty(x => x.Status, AnswerStatus.Scoring)
                        .SetProperty(x => x.LastScoringPublishedAt, now), ct);

                _logger.LogInformation(
                    "Re-published answer {AnswerId}: bù attempt {Missing} (cần {Required}, đã có {Done})",
                    a.Id, string.Join(",", missing), required, done.Count);
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Dời mốc publish thất bại answer {AnswerId}", a.Id);
            }
        }
    }

}
