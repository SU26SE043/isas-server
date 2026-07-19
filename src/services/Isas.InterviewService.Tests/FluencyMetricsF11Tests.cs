using Isas.InterviewService.Data;
using Isas.InterviewService.DTOs;
using Isas.InterviewService.Entities;
using Isas.InterviewService.Enums;
using Isas.InterviewService.Services;
using Isas.InterviewService.Services.Interfaces;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging.Abstractions;
using Moq;

namespace Isas.InterviewService.Tests;

/// <summary>
/// F11 (FR06) — chấm ĐỘ TRÔI CHẢY + phát hiện từ đệm.
///
/// <para><b>Rủi ro số 1 (INT-9)</b>: thêm tiêu chí thứ 7 vào rubric seed mà đường publish và
/// đường callback không chọn CÙNG bộ ⇒ AI chấm thiếu tiêu chí ⇒ answer <c>Failed</c> hàng loạt
/// ⇒ người luyện mất credit (PAY-13). Nên test đi TRỌN chuỗi publish → callback, y như F12.</para>
///
/// <para><b>Rủi ro số 2 (hỏng ÂM THẦM)</b>: chỉ số phải đi được cả <b>hai</b> đường —
/// tĩnh (worker tự transcribe + tự đo) và thích ứng (<c>/decide-next</c> đo, worker bỏ Whisper).
/// Thiếu một đường thì buổi loại đó không có chỉ số, mà KHÔNG có lỗi nào nổ ra: chỉ là tính năng
/// chết một nửa. Ba call site được khoá riêng: vòng adaptive · publish · republisher.</para>
///
/// <para><b>BC16</b>: candidate có rubric RIÊNG thì KHÔNG tự nhận tiêu chí seed mới — đúng thiết
/// kế, không phải bug (xem <see cref="B2CRubricScope"/>).</para>
/// </summary>
public class FluencyMetricsF11Tests
{
    private static readonly JobCategory[] AllCategories =
        [JobCategory.BA, JobCategory.BE, JobCategory.FE];

    private static DeliveryMetricsDto Metrics(
        double wpm = 180, int fillers = 5, int pauses = 3,
        double longestPause = 2.5, double silence = 0.35) => new()
        {
            SpeechRateWpm = wpm,
            FillerCount = fillers,
            PauseCount = pauses,
            LongestPauseSec = longestPause,
            SilenceRatio = silence,
            FillerBreakdown = new Dictionary<string, int> { ["ừm"] = 3, ["kiểu như"] = 2 },
        };

    private static AnswerService Build(
        TestDb t, out List<ScoringJob> jobs, Mock<IAiServiceInterviewDecider>? decider = null)
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
            NullLogger<AnswerService>.Instance, decider?.Object);
    }

    private static AnswerScoreCallbackRequest Callback(
        Guid criterionId, DeliveryMetricsDto? metrics, int attemptNo = 1) => new()
        {
            Transcript = "ừm tôi nghĩ là kiểu như vậy",
            RubricVersion = 1,
            AttemptNo = attemptNo,
            DeliveryMetrics = metrics,
            Scores = { new ScoreItemDto { CriterionId = criterionId, Score = 3m, Reasoning = "ok" } }
        };

    // ── (1) Seed: tiêu chí trôi chảy có ở cả 3 nghề, Σweight vẫn = 1 ──────────────────────
    [Fact]
    public void Seed_MoiNghe_CoTieuChiTroiChay_VaTongWeightVanBang1()
    {
        var seed = B2CRubricSeed.Build();

        foreach (var cat in AllCategories)
        {
            var rows = seed.Where(c => c.JobCategory == cat).ToList();
            Assert.Contains(rows, c => c.Name == B2CRubricSeed.FluencyName);
            // INT-10: rebalance 6 tiêu chí cũ để chừa chỗ cho tiêu chí thứ 7 — Σ phải vẫn đúng 1.
            Assert.Equal(1.0m, rows.Sum(c => c.Weight));
        }
    }

    // ── (2) Tiêu chí trôi chảy KHÔNG được lấn sang chấm nội dung ──────────────────────────
    [Fact]
    public void TieuChiTroiChay_ChiXetCachNoi_KhongXetKienThuc()
    {
        // Nói chậm ≠ kiến thức kém. Không rào rõ trong mô tả thì AI sẽ trừ điểm chuyên môn của
        // người nói ngập ngừng — hai tiêu chí đo cùng một thứ, và đo sai.
        var desc = B2CRubricSeed.Build()
            .First(c => c.Name == B2CRubricSeed.FluencyName).Description!;

        Assert.Contains("CHỈ xét CÁCH NÓI", desc);
        Assert.Contains("không xét câu trả lời đúng/sai", desc);
    }

    // ── (3) INT-9: publish và callback phải khớp bộ tiêu chí (gồm cả tiêu chí F11) ────────
    [Fact]
    public async Task PublishVaCallback_DungCungBoTieuChi_GomCaTroiChay()
    {
        // Đây là test canh RỦI RO LỚN NHẤT của task: publish gửi N tiêu chí mà callback chỉ nhận
        // N-1 (hoặc ngược lại) ⇒ AI chấm thiếu ⇒ Failed hàng loạt ⇒ mất credit.
        using var t = new TestDb();
        var candidate = Guid.NewGuid();
        t.Db.RubricCriteria.AddRange(B2CRubricSeed.Build());

        var session = TestDb.Session(candidate, SessionStatus.Ready);
        session.JobCategory = JobCategory.BE;
        var q = TestDb.Question(session.Id);
        t.Db.AddRange(session, q);
        await t.Db.SaveChangesAsync();

        var svc = Build(t, out var jobs);
        await svc.UploadAnswerAsync(
            session.Id, q.Id, candidate, new MemoryStream([1, 2, 3]), "audio/webm", 42);

        var published = Assert.Single(jobs);
        Assert.Contains(published.Criteria, c => c.Name == B2CRubricSeed.FluencyName);

        // Callback chấm ĐỦ đúng bộ tiêu chí vừa publish → answer phải Scored, không rơi tiêu chí nào.
        var answer = await t.Db.PracticeAnswers.FirstAsync(a => a.SessionId == session.Id);
        var req = new AnswerScoreCallbackRequest
        {
            Transcript = "trả lời",
            RubricVersion = published.RubricVersion,
            AttemptNo = 1,
            DeliveryMetrics = Metrics(),
        };
        foreach (var c in published.Criteria)
            req.Scores.Add(new ScoreItemDto { CriterionId = c.CriterionId, Score = 3m, Reasoning = "ok" });

        await svc.SaveResultAsync(answer.Id, req);

        var saved = await t.Db.PracticeAnswers.AsNoTracking().Include(a => a.Scores)
            .FirstAsync(a => a.Id == answer.Id);
        Assert.Equal(AnswerStatus.Scored, saved.Status);
        // Không điểm nào bị BỎ ở callback guard (E8) ⇒ publish và callback cùng một bộ.
        Assert.Equal(published.Criteria.Count, saved.Scores.Count);
    }

    // ── (4) Callback lưu chỉ số + surface ra DTO ─────────────────────────────────────────
    [Fact]
    public async Task SaveResult_LuuChiSoTroiChay()
    {
        using var t = new TestDb();
        var session = TestDb.Session(Guid.NewGuid(), SessionStatus.Scoring);
        var q = TestDb.Question(session.Id);
        var crit = TestDb.Criterion(session.JobCategory);
        var answer = TestDb.Answer(session.Id, q.Id, AnswerStatus.Scoring, DateTime.UtcNow, DateTime.UtcNow);
        t.Db.AddRange(session, q, crit, answer);
        await t.Db.SaveChangesAsync();

        await Build(t, out _).SaveResultAsync(answer.Id, Callback(crit.Id, Metrics()));

        var saved = await t.Db.PracticeAnswers.AsNoTracking().FirstAsync(a => a.Id == answer.Id);
        Assert.Equal(180, saved.SpeechRateWpm);
        Assert.Equal(5, saved.FillerCount);
        Assert.Equal(3, saved.PauseCount);
        Assert.Equal(2.5, saved.LongestPauseSec);
        Assert.Equal(0.35, saved.SilenceRatio);
        Assert.NotNull(saved.FillerBreakdown);

        // ⚠ CỐ Ý không assert chuỗi tiếng Việt vào JSON đã serialize: mặc định System.Text.Json
        // escape non-ASCII ("ừm" → "ừm"), nên assert kiểu đó vừa dễ ĐỎ oan vừa (ở chiều
        // ngược lại, DoesNotContain) XANH một cách vô nghĩa. Assert vào cấu trúc đã parse.
        // Đọc ngược ra DTO — nếu không surface được thì người luyện không bao giờ thấy (FR06).
        var read = DeliveryMetricsMapper.Read(saved);
        Assert.NotNull(read);
        Assert.Equal(5, read!.FillerCount);
        Assert.Equal(3, read.FillerBreakdown["ừm"]);
    }

    [Fact]
    public async Task SaveResult_ChuaDoDuoc_TraNull_KhongPhaiSo0()
    {
        // Phân biệt "chưa đo được" với "đo ra 0" là điều kiện để FE hiển thị trung thực:
        // hiện "0 từ đệm" cho một answer chưa hề được đo là nói dối người dùng.
        using var t = new TestDb();
        var session = TestDb.Session(Guid.NewGuid(), SessionStatus.Scoring);
        var q = TestDb.Question(session.Id);
        var crit = TestDb.Criterion(session.JobCategory);
        var answer = TestDb.Answer(session.Id, q.Id, AnswerStatus.Scoring, DateTime.UtcNow, DateTime.UtcNow);
        t.Db.AddRange(session, q, crit, answer);
        await t.Db.SaveChangesAsync();

        await Build(t, out _).SaveResultAsync(answer.Id, Callback(crit.Id, metrics: null));

        var saved = await t.Db.PracticeAnswers.AsNoTracking().Include(a => a.Scores)
            .FirstAsync(a => a.Id == answer.Id);
        Assert.Null(saved.SpeechRateWpm);
        Assert.Null(DeliveryMetricsMapper.Read(saved));
        // Thiếu chỉ số KHÔNG được làm hỏng lượt chấm (PAY-13: Failed = mất credit).
        Assert.Equal(AnswerStatus.Scored, saved.Status);
        Assert.Single(saved.Scores);
    }

    [Fact]
    public async Task SaveResult_WorkerCuGuiNull_KhongXoaChiSoDaLuu()
    {
        // Ca thật: đường THÍCH ỨNG đã ghi chỉ số từ /decide-next, rồi worker (image CŨ, chưa có
        // F11) callback với deliveryMetrics=null. Ghi đè null ở đây là xoá mất số đo ĐÚNG.
        using var t = new TestDb();
        var session = TestDb.Session(Guid.NewGuid(), SessionStatus.Scoring);
        var q = TestDb.Question(session.Id);
        var crit = TestDb.Criterion(session.JobCategory);
        var answer = TestDb.Answer(session.Id, q.Id, AnswerStatus.Scoring, DateTime.UtcNow, DateTime.UtcNow);
        DeliveryMetricsMapper.Apply(answer, Metrics(wpm: 222));
        t.Db.AddRange(session, q, crit, answer);
        await t.Db.SaveChangesAsync();

        await Build(t, out _).SaveResultAsync(answer.Id, Callback(crit.Id, metrics: null));

        var saved = await t.Db.PracticeAnswers.AsNoTracking().FirstAsync(a => a.Id == answer.Id);
        Assert.Equal(222, saved.SpeechRateWpm);
    }

    // ── (5) INT-3: thu âm lại → xoá chỉ số của bản ghi cũ ────────────────────────────────
    [Fact]
    public async Task UploadLai_XoaChiSoCuaBanGhiCu()
    {
        using var t = new TestDb();
        var candidate = Guid.NewGuid();
        var session = TestDb.Session(candidate, SessionStatus.InProgress);
        var q = TestDb.Question(session.Id);
        var crit = TestDb.Criterion(session.JobCategory);
        var answer = TestDb.Answer(session.Id, q.Id, AnswerStatus.Scored, DateTime.UtcNow, DateTime.UtcNow);
        DeliveryMetricsMapper.Apply(answer, Metrics());
        t.Db.AddRange(session, q, crit, answer);
        await t.Db.SaveChangesAsync();

        await Build(t, out _).UploadAnswerAsync(
            session.Id, q.Id, candidate, new MemoryStream([9]), "audio/webm", 30);

        var saved = await t.Db.PracticeAnswers.AsNoTracking().FirstAsync(a => a.Id == answer.Id);
        // Giữ lại = báo "bạn nói 'ừm' 5 lần" cho một bản thu đã bị ghi đè.
        Assert.Null(saved.SpeechRateWpm);
        Assert.Null(saved.FillerCount);
        Assert.Null(saved.FillerBreakdown);
    }

    // ── (6) Đường THÍCH ỨNG: /decide-next đo → lưu → vào ScoringJob ──────────────────────
    [Fact]
    public async Task Adaptive_LuuChiSoTuDecideNext_VaDayVaoScoringJob()
    {
        // Chỗ dễ hỏng âm thầm nhất: worker BỎ Whisper khi job có transcript, nên đây là lần đo
        // DUY NHẤT. Rơi ở bất kỳ mắt xích nào (lưu / publish) là buổi thích ứng không có chỉ số
        // trong khi buổi tĩnh vẫn có — không lỗi, không log.
        using var t = new TestDb();
        var candidate = Guid.NewGuid();
        var session = TestDb.Session(candidate, SessionStatus.Ready);
        session.AdaptiveEnabled = true;
        session.MaxQuestions = 10;
        session.MaxFollowUps = 3;
        var q = TestDb.Question(session.Id);
        var crit = TestDb.Criterion(session.JobCategory);
        t.Db.AddRange(session, q, crit);
        await t.Db.SaveChangesAsync();

        var decider = new Mock<IAiServiceInterviewDecider>();
        decider
            .Setup(x => x.DecideNextAsync(
                It.IsAny<string>(), It.IsAny<string>(), It.IsAny<string>(),
                It.IsAny<IReadOnlyList<DecideTurnDto>>(), It.IsAny<int>(), It.IsAny<int>(),
                It.IsAny<int>(), It.IsAny<int>(), It.IsAny<IReadOnlyList<DecideCriterionDto>>(),
                It.IsAny<CancellationToken>()))
            .ReturnsAsync(new DecideNextResult(
                "end", null, "ừm tôi nghĩ vậy", "r", Metrics(wpm: 165, fillers: 7)));

        var svc = Build(t, out var jobs, decider);
        await svc.UploadAnswerAsync(
            session.Id, q.Id, candidate, new MemoryStream([1]), "audio/webm", 40);

        var saved = await t.Db.PracticeAnswers.AsNoTracking().FirstAsync(a => a.SessionId == session.Id);
        Assert.Equal(165, saved.SpeechRateWpm);
        Assert.Equal(7, saved.FillerCount);

        var job = Assert.Single(jobs);
        Assert.Equal("ừm tôi nghĩ vậy", job.Transcript);   // worker sẽ bỏ Whisper
        Assert.NotNull(job.DeliveryMetrics);                // ⇒ chỉ số PHẢI đi kèm
        Assert.Equal(165, job.DeliveryMetrics!.SpeechRateWpm);
        Assert.Equal(7, job.DeliveryMetrics.FillerCount);
    }

    [Fact]
    public async Task LuongTinh_KhongCoChiSoTruoc_JobKhongMangChiSo()
    {
        // Đường tĩnh: chưa ai đo → job không mang chỉ số → worker tự transcribe rồi tự đo.
        // Nếu chỗ này lỡ gửi một DTO toàn 0 thay vì null, worker sẽ tưởng "đã đo, kết quả 0"
        // và bỏ qua việc đo thật ⇒ mọi buổi tĩnh có chỉ số bịa.
        using var t = new TestDb();
        var candidate = Guid.NewGuid();
        var session = TestDb.Session(candidate, SessionStatus.Ready);
        var q = TestDb.Question(session.Id);
        var crit = TestDb.Criterion(session.JobCategory);
        t.Db.AddRange(session, q, crit);
        await t.Db.SaveChangesAsync();

        var svc = Build(t, out var jobs);
        await svc.UploadAnswerAsync(
            session.Id, q.Id, candidate, new MemoryStream([1]), "audio/webm", 40);

        var job = Assert.Single(jobs);
        Assert.Null(job.Transcript);
        Assert.Null(job.DeliveryMetrics);
    }

    // ── (7) Republisher cũng phải mang chỉ số ───────────────────────────────────────────
    [Fact]
    public void MapperRead_TraNullKhiChuaDo_VaTraDuLieuKhiDaDo()
    {
        // Republisher đọc bằng projection (không có entity) nên dùng overload theo từng giá trị;
        // hai overload phải cho cùng kết quả, nếu không thì answer được cứu bằng republisher sẽ
        // mất chỉ số trong khi answer chấm trơn tru vẫn có.
        Assert.Null(DeliveryMetricsMapper.Read(null, null, null, null, null, null));

        var read = DeliveryMetricsMapper.Read(180, 5, 3, 2.5, 0.35, """{"ừm":3}""");
        Assert.NotNull(read);
        Assert.Equal(180, read!.SpeechRateWpm);
        Assert.Equal(3, read.FillerBreakdown["ừm"]);
    }

    [Fact]
    public void MapperRead_JsonHongKhongLamNoDuongCham()
    {
        // JSON hỏng là chuyện lạ (ta tự ghi) — nhưng ném ở đây sẽ làm answer Failed = mất credit.
        var read = DeliveryMetricsMapper.Read(180, 5, 3, 2.5, 0.35, "{không phải json}");
        Assert.NotNull(read);
        Assert.Empty(read!.FillerBreakdown);
        Assert.Equal(5, read.FillerCount);
    }

    // ── (7b) VÁ 2026-07-19 — 9 field phải SỐNG SÓT qua vòng lưu-đọc ────────────────────
    //
    // Lỗi được vá: DTO khai 9 field, `Apply()` chỉ ghi 5 (+breakdown) ⇒ `Read()` dựng lại với
    // audioSec/speechSec/wordCount/fillerPer100Words = 0. Cả hai đường đẩy job chấm
    // (`AnswerService` thích ứng · `StuckAnswerRepublisher` cứu) đều đi qua `Read()`, nên số 0
    // bịa đó vào thẳng prompt chấm dưới nhãn "số liệu thật".

    [Fact]
    public async Task VaF11_ApplyRoiRead_GiuDuCa9Field_KhongMat4FieldThoiGian()
    {
        using var t = new TestDb();
        var session = TestDb.Session(Guid.NewGuid(), SessionStatus.Scoring);
        var q = TestDb.Question(session.Id);
        var crit = TestDb.Criterion(session.JobCategory);
        var answer = TestDb.Answer(session.Id, q.Id, AnswerStatus.Scoring, DateTime.UtcNow, DateTime.UtcNow);
        t.Db.AddRange(session, q, crit, answer);
        await t.Db.SaveChangesAsync();

        var sent = Metrics();
        sent.AudioSec = 62.5;
        sent.SpeechSec = 48.25;
        sent.WordCount = 143;
        sent.FillerPer100Words = 3.5;

        await Build(t, out _).SaveResultAsync(answer.Id, Callback(crit.Id, sent));

        var saved = await t.Db.PracticeAnswers.AsNoTracking().FirstAsync(a => a.Id == answer.Id);

        // (a) 4 cột mới phải THỰC SỰ được ghi xuống — trước bản vá `Apply()` bỏ qua chúng.
        Assert.Equal(62.5, saved.AudioSec);
        Assert.Equal(48.25, saved.SpeechSec);
        Assert.Equal(143, saved.WordCount);
        Assert.Equal(3.5, saved.FillerPer100Words);

        // (b) và phải đọc ngược ra được — đây mới là thứ đi vào prompt chấm + màn kết quả.
        var read = DeliveryMetricsMapper.Read(saved);
        Assert.NotNull(read);
        Assert.Equal(62.5, read!.AudioSec);
        Assert.Equal(48.25, read.SpeechSec);
        Assert.Equal(143, read.WordCount);
        Assert.Equal(3.5, read.FillerPer100Words);
    }

    [Fact]
    public void VaF11_FieldKhuyet_RaNull_KhongPhaiSo0()
    {
        // Đây là bất biến bị vi phạm trước bản vá: `Read()` `?? 0` từng field nên khuyết-vĩnh-viễn
        // biến thành "0" trông y như số đo thật. "0 lần/100 âm tiết" đọc ra như một LỜI KHEN.
        var read = DeliveryMetricsMapper.Read(
            speechRateWpm: 180, fillerCount: 5, pauseCount: 3,
            longestPauseSec: 2.5, silenceRatio: 0.35, fillerBreakdownJson: null);

        Assert.NotNull(read);
        Assert.Equal(180, read!.SpeechRateWpm);   // field đo được: có số
        Assert.Null(read.AudioSec);               // field khuyết: PHẢI null, không phải 0
        Assert.Null(read.SpeechSec);
        Assert.Null(read.WordCount);
        Assert.Null(read.FillerPer100Words);
    }

    [Fact]
    public void VaF11_ChiCo4FieldMoi_VanCoiLaDaDo()
    {
        // Ngữ nghĩa cả-cụm: null chỉ khi KHÔNG có số nào. Nếu chỉ 4 field mới có giá trị mà vẫn
        // trả null thì worker sẽ tưởng "chưa đo" rồi transcribe + đo lại từ đầu — tốn một lượt
        // Whisper cho dữ liệu đã nằm sẵn trong DB.
        var read = DeliveryMetricsMapper.Read(
            speechRateWpm: null, fillerCount: null, pauseCount: null,
            longestPauseSec: null, silenceRatio: null, fillerBreakdownJson: null,
            audioSec: 30.0, speechSec: 25.0, wordCount: 80, fillerPer100Words: 1.25);

        Assert.NotNull(read);
        Assert.Equal(30.0, read!.AudioSec);
        Assert.Null(read.SpeechRateWpm);
    }

    // ── (8) BC16 — rubric riêng KHÔNG tự nhận tiêu chí seed mới ─────────────────────────
    [Fact]
    public async Task BC16_RubricRieng_KhongTuNhanTieuChiTroiChay()
    {
        // Đúng thiết kế, KHÔNG phải bug: người đã tự khai rubric riêng thì bộ tiêu chí của họ do
        // họ quyết. Hệ quả cần team biết: FR06 chỉ phủ nhóm dùng rubric seed.
        using var t = new TestDb();
        var candidate = Guid.NewGuid();
        t.Db.RubricCriteria.AddRange(B2CRubricSeed.Build());

        var own = TestDb.Criterion(JobCategory.BE);
        own.CandidateId = candidate;
        own.Name = "Tiêu chí của riêng tôi";
        t.Db.RubricCriteria.Add(own);

        var session = TestDb.Session(candidate, SessionStatus.Ready);
        session.JobCategory = JobCategory.BE;
        var q = TestDb.Question(session.Id);
        t.Db.AddRange(session, q);
        await t.Db.SaveChangesAsync();

        var svc = Build(t, out var jobs);
        await svc.UploadAnswerAsync(
            session.Id, q.Id, candidate, new MemoryStream([1]), "audio/webm", 30);

        var job = Assert.Single(jobs);
        Assert.DoesNotContain(job.Criteria, c => c.Name == B2CRubricSeed.FluencyName);
        Assert.Contains(job.Criteria, c => c.Name == "Tiêu chí của riêng tôi");
    }
}
