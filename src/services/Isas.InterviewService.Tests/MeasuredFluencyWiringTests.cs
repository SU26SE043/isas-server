using System.Reflection;
using Isas.InterviewService.ApplicationDbContext;
using Isas.InterviewService.Data;
using Isas.InterviewService.DTOs;
using Isas.InterviewService.Entities;
using Isas.InterviewService.Enums;
using Isas.InterviewService.Models;
using Isas.InterviewService.Services;
using Isas.InterviewService.Services.Interfaces;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using Moq;

namespace Isas.InterviewService.Tests;

/// <summary>
/// Đấu dây tiêu chí chấm bằng SỐ ĐO vào đường chấm thật: publish → callback → điểm buổi.
///
/// <para>Bộ test của <see cref="DeliveryFluencyScorerTests"/> chứng minh phép TÍNH đúng; bộ này
/// chứng minh nó THẬT SỰ ĐƯỢC GỌI. Đây là hai thứ khác nhau — lỗ đã có tiền lệ trong repo: mapper
/// có test, retry-feedback có test, nhưng KHE NỐI giữa chúng thì không, mà bug lại sống đúng ở đó.</para>
/// </summary>
public class MeasuredFluencyWiringTests
{
    private static DeliveryMetricsDto Metrics(
        double silence = 0.144, double speechSec = 42, int pauses = 1, double wpm = 199) => new()
        {
            SilenceRatio = silence,
            SpeechSec = speechSec,
            PauseCount = pauses,
            SpeechRateWpm = wpm,
            AudioSec = speechSec * 1.4,
            WordCount = 140,
            MetricsVersion = 2,
        };

    private static AnswerService Build(TestDb t, out List<ScoringJob> jobs, DeliveryScoringOptions? delivery = null)
    {
        var published = new List<ScoringJob>();
        var publisher = new Mock<IScoringJobPublisher>();
        publisher
            .Setup(p => p.PublishAsync(It.IsAny<ScoringJob>(), It.IsAny<CancellationToken>()))
            .Callback<ScoringJob, CancellationToken>((j, _) => published.Add(j))
            .Returns(Task.CompletedTask);
        jobs = published;

        var storage = new Mock<IStorageService>();
        storage
            .Setup(s => s.UploadAsync(
                It.IsAny<Stream>(), It.IsAny<string>(), It.IsAny<Guid>(),
                It.IsAny<Guid>(), It.IsAny<string>(), It.IsAny<string>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync("answer-audio/seed.webm");

        return new AnswerService(
            t.Db, storage.Object, publisher.Object,
            new Mock<ISessionScoringNotifier>().Object, TestDb.ScoringOpts(),
            NullLogger<AnswerService>.Instance,
            deliveryScoringOptions: Options.Create(delivery ?? new DeliveryScoringOptions()));
    }

    private static Guid FluencyId(InterviewDbContext db, JobCategory cat = JobCategory.BE)
        => db.RubricCriteria.Single(c =>
            c.CampaignId == null && c.CandidateId == null && c.JobCategory == cat
            && c.Language == "vi" && c.ScoringMethod == CriterionScoringMethod.DeliveryMetrics).Id;

    private static async Task<(PracticeSession s, PracticeQuestion q, PracticeAnswer a)> SeedAsync(
        TestDb t, Guid candidate, DeliveryMetricsDto? metrics)
    {
        t.Db.RubricCriteria.AddRange(B2CRubricSeed.Build());
        var s = TestDb.Session(candidate, SessionStatus.InProgress, cat: JobCategory.BE);
        var q = TestDb.Question(s.Id);
        var a = TestDb.Answer(s.Id, q.Id, AnswerStatus.Scoring, DateTime.UtcNow, DateTime.UtcNow);
        DeliveryMetricsMapper.Apply(a, metrics);
        t.Db.AddRange(s, q, a);
        await t.Db.SaveChangesAsync();
        return (s, q, a);
    }

    private static AnswerScoreCallbackRequest Callback(
        InterviewDbContext db, DeliveryMetricsDto? metrics, bool includeFluencyFromAi = false,
        string transcript = "tôi nghĩ là như vậy")
    {
        var criteria = db.RubricCriteria.AsNoTracking()
            .Where(c => c.CampaignId == null && c.CandidateId == null
                        && c.JobCategory == JobCategory.BE && c.Language == "vi")
            .ToList()
            .Where(c => includeFluencyFromAi || c.ScoringMethod == CriterionScoringMethod.Ai);

        return new AnswerScoreCallbackRequest
        {
            Transcript = transcript,
            RubricVersion = B2CRubricSeed.RubricVersion,
            AttemptNo = 1,
            DeliveryMetrics = metrics,
            Scores = criteria
                .Select(c => new ScoreItemDto { CriterionId = c.Id, Score = 4m, Reasoning = "ok" })
                .ToList(),
        };
    }

    // ── (1) Đường PUBLISH: tiêu chí đo được KHÔNG đi vào bộ gửi LLM ──────────────────────
    [Fact]
    public async Task Publish_KhongGuiTieuChiChamBangSoDo_ChoLLM()
    {
        using var t = new TestDb();
        var candidate = Guid.NewGuid();
        t.Db.RubricCriteria.AddRange(B2CRubricSeed.Build());
        var s = TestDb.Session(candidate, SessionStatus.Ready, cat: JobCategory.BE);
        var q = TestDb.Question(s.Id);
        t.Db.AddRange(s, q);
        await t.Db.SaveChangesAsync();

        var svc = Build(t, out var jobs);
        await svc.UploadAnswerAsync(s.Id, q.Id, candidate, new MemoryStream([1, 2, 3]), "audio/webm", 42);

        var job = Assert.Single(jobs);
        var fluency = FluencyId(t.Db);
        Assert.DoesNotContain(job.Criteria, c => c.CriterionId == fluency);
        // Gửi thiếu KHÁC gửi rỗng: 6 tiêu chí còn lại phải nguyên vẹn, không thì INT-9 đánh Failed.
        Assert.Equal(6, job.Criteria.Count);
    }

    // ── (2) Đường REPUBLISHER phải áp CÙNG luật ──────────────────────────────────────────
    [Fact]
    public async Task Republisher_ApCungLuat_KhongGuiTieuChiDoDuoc()
    {
        // Hai đường publish lệch luật là lỗi chỉ lộ ra KHI ĐÃ CÓ SỰ CỐ: answer nào phải nhờ đường
        // cứu hộ sẽ bị LLM chấm độ trôi chảy còn answer đường thường thì không, và median E10 gộp
        // hai thước đo mà không có triệu chứng nào. Chính cặp đường này đã dính đúng lỗi đó ở F11.
        using var t = new TestDb();
        var candidate = Guid.NewGuid();
        t.Db.RubricCriteria.AddRange(B2CRubricSeed.Build());
        var s = TestDb.Session(candidate, SessionStatus.InProgress, cat: JobCategory.BE);
        var q = TestDb.Question(s.Id);
        // Answer KẸT: `Uploaded`, chưa publish lần nào, tạo cách đây 10' (quá PublishFailedMinutes
        // nhưng vẫn trong `Scoring:GiveUpAfterMinutes` = 20' — quá trần thì republisher THÔI đẩy và
        // test không còn đo được gì).
        var a = TestDb.Answer(s.Id, q.Id, AnswerStatus.Uploaded, DateTime.UtcNow.AddMinutes(-10), lastPublished: null);
        DeliveryMetricsMapper.Apply(a, Metrics());
        t.Db.AddRange(s, q, a);
        await t.Db.SaveChangesAsync();

        var services = new ServiceCollection();
        services.AddDbContext<InterviewDbContext>(o =>
            o.UseSqlite(t.Connection).UseSnakeCaseNamingConvention());
        var provider = services.BuildServiceProvider();

        var pub = new Mock<IScoringJobPublisher>();
        var sent = new List<ScoringJob>();
        pub.Setup(p => p.PublishAsync(It.IsAny<ScoringJob>(), It.IsAny<CancellationToken>()))
            .Callback<ScoringJob, CancellationToken>((j, _) => sent.Add(j))
            .Returns(Task.CompletedTask);

        var r = new StuckAnswerRepublisher(
            provider.GetRequiredService<IServiceScopeFactory>(), pub.Object,
            Options.Create(new RepublisherSettings { BatchSize = 200 }),
            Options.Create(new ScoringOptions()),
            NullLogger<StuckAnswerRepublisher>.Instance);

        await (Task)typeof(StuckAnswerRepublisher)
            .GetMethod("ScanOnceAsync", BindingFlags.NonPublic | BindingFlags.Instance)!
            .Invoke(r, [CancellationToken.None])!;

        var job = Assert.Single(sent);
        Assert.DoesNotContain(job.Criteria, c => c.CriterionId == FluencyId(t.Db));
        Assert.Equal(6, job.Criteria.Count);
    }

    // ── (3) Callback GHI điểm tính từ số đo, kèm con dấu phiên bản ───────────────────────
    [Fact]
    public async Task Callback_GhiDiemTuSoDo_KemConDauPhienBan()
    {
        using var t = new TestDb();
        var candidate = Guid.NewGuid();
        var (_, _, a) = await SeedAsync(t, candidate, Metrics(silence: 0.144));

        await Build(t, out _).SaveResultAsync(a.Id, Callback(t.Db, Metrics(silence: 0.144)));

        var fluency = FluencyId(t.Db);
        var row = await t.Db.AnswerScores.AsNoTracking()
            .SingleAsync(x => x.AnswerId == a.Id && x.CriterionId == fluency);

        Assert.Equal(4m, row.Score);                       // silence 0.144 = p50 → bậc 4
        Assert.Equal(1, row.DeliveryScoringVersion);       // con dấu bộ ngưỡng
        Assert.Null(row.PromptVersion);                    // không prompt nào tham gia
        Assert.Null(row.LevelMatched);                     // mức neo E9 là khái niệm của đường LLM
        Assert.False(string.IsNullOrWhiteSpace(row.Reasoning));

        // Dòng do LLM chấm KHÔNG được mang con dấu này — nó là cách nhận diện đáng tin duy nhất.
        Assert.All(
            await t.Db.AnswerScores.AsNoTracking()
                .Where(x => x.AnswerId == a.Id && x.CriterionId != fluency).ToListAsync(),
            x => Assert.Null(x.DeliveryScoringVersion));
    }

    // ── (4) 🔴 BUG PRODUCTION: cùng file ghi âm, câu hỏi khác nhau ⇒ PHẢI cùng điểm ───────
    [Fact]
    public async Task CungBanGhi_BonCauHoiKhacNhau_LuonRaCungMotDiemTroiChay()
    {
        // Tái hiện đúng phép đo đã phơi ra bug: cùng một file ghi âm nộp cho 4 câu khác nhau từng
        // nhận 0% · 40% · 60%. Ở đây transcript và điểm nội dung LLM trả về cố ý khác nhau hoàn
        // toàn giữa 4 lượt — nếu nội dung còn đường lây sang cách nói thì test này ĐỎ.
        using var t = new TestDb();
        var candidate = Guid.NewGuid();
        t.Db.RubricCriteria.AddRange(B2CRubricSeed.Build());
        var s = TestDb.Session(candidate, SessionStatus.InProgress, cat: JobCategory.BE);
        t.Db.Add(s);

        var metrics = Metrics(silence: 0.2, speechSec: 55, pauses: 2);
        var answers = new List<PracticeAnswer>();
        for (var i = 1; i <= 4; i++)
        {
            var q = TestDb.Question(s.Id, order: i);
            var a = TestDb.Answer(s.Id, q.Id, AnswerStatus.Scoring, DateTime.UtcNow, DateTime.UtcNow);
            DeliveryMetricsMapper.Apply(a, metrics);   // CÙNG MỘT bộ số đo cho cả 4
            t.Db.AddRange(q, a);
            answers.Add(a);
        }
        await t.Db.SaveChangesAsync();

        var svc = Build(t, out _);
        var aiScores = new[] { 0m, 2m, 3m, 5m };   // nội dung: từ lạc đề tới xuất sắc
        for (var i = 0; i < 4; i++)
        {
            var req = Callback(t.Db, metrics, transcript: $"câu trả lời số {i} hoàn toàn khác nhau");
            foreach (var item in req.Scores) item.Score = aiScores[i];
            await svc.SaveResultAsync(answers[i].Id, req);
        }

        var fluency = FluencyId(t.Db);
        var scores = await t.Db.AnswerScores.AsNoTracking()
            .Where(x => x.CriterionId == fluency)
            .Select(x => x.Score).ToListAsync();

        Assert.Equal(4, scores.Count);
        Assert.Single(scores.Distinct());   // ĐÚNG MỘT giá trị — đây là cả điểm của thay đổi này
    }

    // ── (5) Thiếu số đo ⇒ KHÔNG ghi dòng nào (LOẠI), KHÔNG phải 0 điểm ──────────────────
    [Fact]
    public async Task ThieuSoDo_LoaiTieuChiKhoiDiem_KhongGhiDong0()
    {
        using var t = new TestDb();
        var candidate = Guid.NewGuid();
        var (_, _, a) = await SeedAsync(t, candidate, metrics: null);

        await Build(t, out _).SaveResultAsync(a.Id, Callback(t.Db, metrics: null));

        var fluency = FluencyId(t.Db);
        Assert.Empty(await t.Db.AnswerScores.AsNoTracking()
            .Where(x => x.AnswerId == a.Id && x.CriterionId == fluency).ToListAsync());
        // 6 tiêu chí LLM vẫn được lưu bình thường — "loại một tiêu chí" không được kéo theo cái nào khác.
        Assert.Equal(6, await t.Db.AnswerScores.CountAsync(x => x.AnswerId == a.Id));
    }

    [Fact]
    public async Task DuoiSanThoiLuongNoi_CungBiLoai()
    {
        using var t = new TestDb();
        var candidate = Guid.NewGuid();
        var (_, _, a) = await SeedAsync(t, candidate, Metrics(speechSec: 3));

        await Build(t, out _).SaveResultAsync(a.Id, Callback(t.Db, Metrics(speechSec: 3)));

        Assert.Empty(await t.Db.AnswerScores.AsNoTracking()
            .Where(x => x.AnswerId == a.Id && x.CriterionId == FluencyId(t.Db)).ToListAsync());
    }

    // ── (6) Điểm LLM trả về cho tiêu chí đo được PHẢI bị bỏ ─────────────────────────────
    [Fact]
    public async Task DiemLLMChoTieuChiDoDuoc_BiBo_KhongGhiDe()
    {
        // Với tới được thật: image AIService lệch, job cũ còn trong queue, hoặc nhánh lùi-an-toàn
        // đã gửi nguyên bộ. Cả ba ca đều KHÔNG được phép ghi đè con số hệ tự tính — hai nguồn cho
        // cùng một tiêu chí tệ hơn cả cái bug đang sửa.
        using var t = new TestDb();
        var candidate = Guid.NewGuid();
        var (_, _, a) = await SeedAsync(t, candidate, Metrics(silence: 0.144));

        var req = Callback(t.Db, Metrics(silence: 0.144), includeFluencyFromAi: true);
        var fluency = FluencyId(t.Db);
        req.Scores.Single(x => x.CriterionId == fluency).Score = 0m;   // LLM zero oan như production

        await Build(t, out _).SaveResultAsync(a.Id, req);

        var row = await t.Db.AnswerScores.AsNoTracking()
            .SingleAsync(x => x.AnswerId == a.Id && x.CriterionId == fluency);
        Assert.Equal(4m, row.Score);                  // số đo thắng, KHÔNG phải 0 của LLM
        Assert.Equal(1, row.DeliveryScoringVersion);
    }

    // ── (7) Tiêu chí bị loại rơi khỏi MẪU SỐ điểm buổi (INT-10), không bị tính 0 ────────
    [Fact]
    public async Task TieuChiBiLoai_RoiKhoiMauSoDiemBuoi()
    {
        using var t = new TestDb();
        var candidate = Guid.NewGuid();
        var (s, _, a) = await SeedAsync(t, candidate, metrics: null);

        await Build(t, out _).SaveResultAsync(a.Id, Callback(t.Db, metrics: null));
        await TestDb.ResultService(t.Db).ComputeAndStoreAsync(s.Id);

        var rows = await t.Db.SessionCriterionScores.AsNoTracking()
            .Where(x => x.SessionId == s.Id).ToListAsync();

        // 6 dòng, KHÔNG có dòng trôi chảy — và tuyệt đối không có dòng 0 điểm nào.
        Assert.Equal(6, rows.Count);
        Assert.DoesNotContain(rows, x => x.CriterionName == B2CRubricSeed.FluencyName);
        Assert.DoesNotContain(rows, x => x.Percentage == 0m);

        // Điểm tổng = trung bình 6 tiêu chí đã chấm (4/5 = 80%), KHÔNG bị một số 0 bịa kéo xuống 68,6%.
        var session = await t.Db.PracticeSessions.AsNoTracking().SingleAsync(x => x.Id == s.Id);
        Assert.Equal(80m, session.OverallScore);
    }

    // ── (8) E10: N attempt ⇒ N dòng giống hệt ⇒ spread = 0 ⇒ KHÔNG needs_review giả ─────
    [Fact]
    public async Task NhieuAttempt_DiemDoDuocKhongDaoDong_KhongSinhNeedsReviewGia()
    {
        using var t = new TestDb();
        var candidate = Guid.NewGuid();
        var (_, _, a) = await SeedAsync(t, candidate, Metrics());

        var svc = Build(t, out _);
        for (var attempt = 1; attempt <= 3; attempt++)
        {
            var req = Callback(t.Db, Metrics());
            req.AttemptNo = attempt;
            await svc.SaveResultAsync(a.Id, req);
        }

        var fluency = FluencyId(t.Db);
        var rows = await t.Db.AnswerScores.AsNoTracking()
            .Where(x => x.AnswerId == a.Id && x.CriterionId == fluency).ToListAsync();

        Assert.Equal(3, rows.Count);
        Assert.Equal([1, 2, 3], rows.Select(x => x.AttemptNo).OrderBy(x => x));
        Assert.Single(rows.Select(x => x.Score).Distinct());   // số đo tất định ⇒ spread = 0
    }

    // ── (9) Idempotency: gửi lại cùng attempt KHÔNG nhân đôi dòng ───────────────────────
    [Fact]
    public async Task GuiLaiCungAttempt_KhongNhanDoiDongDiemDoDuoc()
    {
        using var t = new TestDb();
        var candidate = Guid.NewGuid();
        var (_, _, a) = await SeedAsync(t, candidate, Metrics());

        var svc = Build(t, out _);
        await svc.SaveResultAsync(a.Id, Callback(t.Db, Metrics()));
        await svc.SaveResultAsync(a.Id, Callback(t.Db, Metrics()));

        Assert.Single(await t.Db.AnswerScores.AsNoTracking()
            .Where(x => x.AnswerId == a.Id && x.CriterionId == FluencyId(t.Db)).ToListAsync());
    }

    // ── (10) Kill-switch: tắt ⇒ quay lại nhờ LLM chấm y như trước ───────────────────────
    [Fact]
    public async Task TatKillSwitch_QuayLaiNhoLLMCham()
    {
        using var t = new TestDb();
        var candidate = Guid.NewGuid();
        t.Db.RubricCriteria.AddRange(B2CRubricSeed.Build());
        var s = TestDb.Session(candidate, SessionStatus.Ready, cat: JobCategory.BE);
        var q = TestDb.Question(s.Id);
        t.Db.AddRange(s, q);
        await t.Db.SaveChangesAsync();

        var off = new DeliveryScoringOptions { Enabled = false };
        var svc = Build(t, out var jobs, off);
        await svc.UploadAnswerAsync(s.Id, q.Id, candidate, new MemoryStream([1]), "audio/webm", 42);

        // ⚠ Kill-switch chỉ tắt phép TÍNH, KHÔNG bật lại việc gửi tiêu chí cho LLM — bộ tiêu chí gửi
        // đi là thuộc tính của RUBRIC (cột `scoring_method`), không phải của cấu hình chấm. Tắt cờ
        // mà rubric vẫn khai `DeliveryMetrics` thì tiêu chí đó đơn giản là KHÔNG được chấm nữa.
        // Muốn trả nó về cho LLM thì đổi `scoring_method` của rubric, và điều đó là CÓ CHỦ ĐÍCH:
        // một cờ cấu hình không nên âm thầm đổi bộ tiêu chí đi vào prompt.
        Assert.DoesNotContain(Assert.Single(jobs).Criteria, c => c.CriterionId == FluencyId(t.Db));

        var a = await t.Db.PracticeAnswers.SingleAsync(x => x.SessionId == s.Id);
        await svc.SaveResultAsync(a.Id, Callback(t.Db, Metrics()));
        Assert.Empty(await t.Db.AnswerScores.AsNoTracking()
            .Where(x => x.AnswerId == a.Id && x.CriterionId == FluencyId(t.Db)).ToListAsync());
    }

    // ── (11) B2B KHÔNG bị đụng ──────────────────────────────────────────────────────────
    [Fact]
    public async Task TieuChiCampaignB2B_GiuNguyen_VanDoLLMCham()
    {
        using var t = new TestDb();
        var candidate = Guid.NewGuid();
        var campaignId = Guid.NewGuid();

        // Tiêu chí campaign do HR gõ — KHÔNG khai `scoring_method` ⇒ nhận mặc định `Ai`.
        var c1 = TestDb.Criterion(JobCategory.BE, campaignId: campaignId, name: "Độ trôi chảy & tự tin");
        var c2 = TestDb.Criterion(JobCategory.BE, campaignId: campaignId, name: "Kỹ thuật");
        var s = TestDb.Session(candidate, SessionStatus.Ready, cat: JobCategory.BE, campaignId: campaignId);
        var q = TestDb.Question(s.Id);
        t.Db.AddRange(c1, c2, s, q);
        await t.Db.SaveChangesAsync();

        var svc = Build(t, out var jobs);
        await svc.UploadAnswerAsync(s.Id, q.Id, candidate, new MemoryStream([1]), "audio/webm", 42);

        // TRÙNG TÊN với tiêu chí bộ chuẩn nhưng vẫn đi đường LLM: nhận diện bằng CỘT, không bằng TÊN.
        var job = Assert.Single(jobs);
        Assert.Equal(2, job.Criteria.Count);
        Assert.Contains(job.Criteria, c => c.CriterionId == c1.Id);
    }
}
