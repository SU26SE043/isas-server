using System.Reflection;
using Isas.InterviewService.ApplicationDbContext;
using Isas.InterviewService.DTOs;
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
/// Con dấu ENGINE của bản chép (<c>practice_answers.transcript_engine</c>).
///
/// <para><b>Vì sao có:</b> AIService chép transcript qua nhà cung cấp TỪ XA và rơi về Whisper CỤC BỘ
/// khi mạng hỏng ⇒ <b>hai answer trong CÙNG một buổi có thể được chép bằng hai engine khác nhau</b>.
/// Đo thật: Whisper <c>small</c> sai 4,2% số từ (chép "người dùng <b>cần</b> thiết" → "người dùng
/// <b>tầng</b> thiết") trong khi engine từ xa sai 0,5–0,7%. Chất lượng chữ đi thẳng vào điểm chấm,
/// mà điểm vẫn bị đem so với nhau ở xếp hạng B2B (CAMP-10) và ở đo cải thiện roadmap (BC15).</para>
///
/// <para><b>Bất biến trung tâm, và nó KHÔNG phải "luôn có dấu":</b> con dấu không bao giờ được mô tả
/// SAI bản chép đang nằm cạnh nó. Khuyết dấu là chấp nhận được ("không biết"); dấu sai thì trả lời
/// sai một cách tự tin và không ai có cách nào phát hiện — nguyên tắc đã chốt ở BK23.</para>
///
/// <para><b>Bất biến an toàn:</b> đây là cột KIỂM TOÁN. Thiếu/rác không được phép làm answer
/// <c>Failed</c> — Failed = người luyện mất 1 credit (PAY-13). Mẫu: F13 · F11 · BK23.</para>
/// </summary>
public class TranscriptEngineStampTests
{
    // Sentinel ASCII: tên engine thật (whisper-1 / gemini-2.5-flash) vốn đã ASCII nên không dính bẫy
    // escape non-ASCII của System.Text.Json, nhưng vẫn đặt tên rõ để đọc log mutation cho nhanh.
    private const string RemoteEngine = "whisper-1";
    private const string LocalEngine = "whisper-local-small";

    // ── Hạ tầng dựng service ────────────────────────────────────────────────────
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
        Guid criterionId, string transcript, string? engine, int attemptNo = 1) => new()
        {
            Transcript = transcript,
            TranscriptEngine = engine,
            RubricVersion = 1,
            AttemptNo = attemptNo,
            Scores = { new ScoreItemDto { CriterionId = criterionId, Score = 3m, Reasoning = "ok" } }
        };

    private static async Task<(Guid critId, Guid answerId)> SeedScoringAsync(
        TestDb t, string? transcript = null, string? engine = null)
    {
        var session = TestDb.Session(Guid.NewGuid(), SessionStatus.Scoring);
        var q = TestDb.Question(session.Id);
        var crit = TestDb.Criterion(session.JobCategory);
        var answer = TestDb.Answer(session.Id, q.Id, AnswerStatus.Scoring, DateTime.UtcNow, DateTime.UtcNow);
        answer.Transcript = transcript;
        answer.TranscriptEngine = engine;
        t.Db.AddRange(session, q, crit, answer);
        await t.Db.SaveChangesAsync();
        return (crit.Id, answer.Id);
    }

    // ── (1) Đường TĨNH: worker tự chép, tự đóng dấu ─────────────────────────────

    [Fact]
    public async Task Callback_LuuConDauEngine()
    {
        // Bất biến gốc của cả bản vá: chấm xong thì biết bản chép đến từ engine nào.
        using var t = new TestDb();
        var (critId, answerId) = await SeedScoringAsync(t);

        await Build(t, out _).SaveResultAsync(answerId, Callback(critId, "tôi nghĩ vậy", RemoteEngine));

        var saved = await t.Db.PracticeAnswers.AsNoTracking().FirstAsync(a => a.Id == answerId);
        Assert.Equal(RemoteEngine, saved.TranscriptEngine);
        Assert.Equal("tôi nghĩ vậy", saved.Transcript);
    }

    [Fact]
    public async Task Callback_HaiAnswerHaiEngine_MoiCaiGiuDauCuaChinhNo()
    {
        // Đây CHÍNH LÀ tình huống bản vá tồn tại để hiện ra: cùng một buổi, câu thứ hai rơi về
        // Whisper cục bộ vì mạng chập. Nếu con dấu không per-answer thì chênh lệch 4,2% vs 0,5%
        // biến mất khỏi dữ liệu và điểm hai câu vẫn được cộng/so như thể cùng chất lượng đầu vào.
        using var t = new TestDb();
        var session = TestDb.Session(Guid.NewGuid(), SessionStatus.Scoring);
        var q1 = TestDb.Question(session.Id, order: 1);
        var q2 = TestDb.Question(session.Id, order: 2);
        var crit = TestDb.Criterion(session.JobCategory);
        var a1 = TestDb.Answer(session.Id, q1.Id, AnswerStatus.Scoring, DateTime.UtcNow, DateTime.UtcNow);
        var a2 = TestDb.Answer(session.Id, q2.Id, AnswerStatus.Scoring, DateTime.UtcNow, DateTime.UtcNow);
        t.Db.AddRange(session, q1, q2, crit, a1, a2);
        await t.Db.SaveChangesAsync();

        var svc = Build(t, out _);
        await svc.SaveResultAsync(a1.Id, Callback(crit.Id, "cau mot", RemoteEngine));
        await svc.SaveResultAsync(a2.Id, Callback(crit.Id, "cau hai", LocalEngine));

        var saved = await t.Db.PracticeAnswers.AsNoTracking()
            .Where(a => a.SessionId == session.Id).ToListAsync();
        Assert.Equal(RemoteEngine, saved.Single(a => a.Id == a1.Id).TranscriptEngine);
        Assert.Equal(LocalEngine, saved.Single(a => a.Id == a2.Id).TranscriptEngine);
    }

    // ── (2) Worker/image CŨ — không gửi dấu ─────────────────────────────────────

    [Fact]
    public async Task Callback_WorkerCu_KhongGuiDau_VanChamXong_DauNull()
    {
        // Deploy AIService lệch nhịp .NET là chuyện thường ở đây. Phải: điểm vẫn lưu, answer vẫn
        // Scored, dấu để NULL — KHÔNG Failed, KHÔNG mất credit (PAY-13).
        using var t = new TestDb();
        var (critId, answerId) = await SeedScoringAsync(t);

        await Build(t, out _).SaveResultAsync(answerId, Callback(critId, "tôi nghĩ vậy", engine: null));

        var saved = await t.Db.PracticeAnswers.AsNoTracking().Include(a => a.Scores)
            .FirstAsync(a => a.Id == answerId);
        Assert.Null(saved.TranscriptEngine);
        Assert.Equal(AnswerStatus.Scored, saved.Status);
        Assert.Single(saved.Scores);
    }

    [Fact]
    public async Task Callback_WorkerCu_ChepLaiBanMOI_KhongGiuDauCu()
    {
        // Ca tinh vi nhất của cả bộ này. Answer đang mang dấu của bản chép TRƯỚC; worker cũ chép
        // lại (transcript ĐỔI) mà không kèm dấu. Giữ dấu cũ = gán lai lịch bản chép trước cho bản
        // chép sau ⇒ con dấu NÓI DỐI, và nói dối theo cách không ai kiểm được. "Không biết" đúng hơn.
        using var t = new TestDb();
        var (critId, answerId) = await SeedScoringAsync(t, transcript: "ban chep cu", engine: RemoteEngine);

        await Build(t, out _).SaveResultAsync(answerId, Callback(critId, "ban chep MOI", engine: null));

        var saved = await t.Db.PracticeAnswers.AsNoTracking().FirstAsync(a => a.Id == answerId);
        Assert.Equal("ban chep MOI", saved.Transcript);
        Assert.Null(saved.TranscriptEngine);
    }

    [Fact]
    public async Task Callback_WorkerCu_EchoLaiDungBanChepCu_GIU_dau()
    {
        // Vế ĐỐI của test trên, và là ca THƯỜNG TRỰC của đường thích ứng: job mang sẵn transcript ⇒
        // worker bỏ Whisper ⇒ nó echo lại đúng bản chép đó. Bản chép không đổi thì dấu cũ vẫn mô tả
        // đúng nó. Xoá ở đây là tự tay đánh mất con dấu ĐÚNG mỗi lần image AIService lệch nhịp.
        using var t = new TestDb();
        var (critId, answerId) = await SeedScoringAsync(t, transcript: "ban chep cu", engine: RemoteEngine);

        await Build(t, out _).SaveResultAsync(answerId, Callback(critId, "ban chep cu", engine: null));

        var saved = await t.Db.PracticeAnswers.AsNoTracking().FirstAsync(a => a.Id == answerId);
        Assert.Equal(RemoteEngine, saved.TranscriptEngine);
    }

    // ── (3) Rác từ worker → null, KHÔNG cắt cụt, KHÔNG ném ──────────────────────

    [Fact]
    public async Task Callback_DauRong_LuuNull_KhongLuuChuoiRong()
    {
        // "" và "   " không phải tên engine. Lưu chúng làm dấu là tạo ra một giá trị trông như có
        // thật nhưng vô nghĩa, phá luôn phép đếm "bao nhiêu answer chưa có dấu".
        using var t = new TestDb();
        var (critId, answerId) = await SeedScoringAsync(t);

        await Build(t, out _).SaveResultAsync(answerId, Callback(critId, "x", engine: "   "));

        var saved = await t.Db.PracticeAnswers.AsNoTracking().FirstAsync(a => a.Id == answerId);
        Assert.Null(saved.TranscriptEngine);
    }

    [Fact]
    public async Task Callback_DauQuaDai_BoHan_KhongCatCut_VanChamXong()
    {
        // Cắt cụt sẽ đẻ ra một tên engine CHƯA TỪNG TỒN TẠI rồi lưu nó như sự thật — đúng thứ cột
        // này sinh ra để tránh. Và tuyệt đối không ném: cột kiểm toán không được thành đường Failed.
        using var t = new TestDb();
        var (critId, answerId) = await SeedScoringAsync(t);
        var rac = new string('x', 500);

        await Build(t, out _).SaveResultAsync(answerId, Callback(critId, "x", engine: rac));

        var saved = await t.Db.PracticeAnswers.AsNoTracking().Include(a => a.Scores)
            .FirstAsync(a => a.Id == answerId);
        Assert.Null(saved.TranscriptEngine);
        Assert.Equal(AnswerStatus.Scored, saved.Status);   // không mất credit
        Assert.Single(saved.Scores);
    }

    [Fact]
    public async Task Callback_DauCoKhoangTrangThua_DuocTrim()
    {
        // Chuỗi thừa khoảng trắng sẽ tách "whisper-1" thành hai giá trị khác nhau khi gom nhóm.
        using var t = new TestDb();
        var (critId, answerId) = await SeedScoringAsync(t);

        await Build(t, out _).SaveResultAsync(answerId, Callback(critId, "x", engine: $"  {RemoteEngine}  "));

        var saved = await t.Db.PracticeAnswers.AsNoTracking().FirstAsync(a => a.Id == answerId);
        Assert.Equal(RemoteEngine, saved.TranscriptEngine);
    }

    // ── (4) Answer cũ (trước bản vá) đọc ra null, không phải giá trị bịa ─────────

    [Fact]
    public async Task AnswerCu_ChuaTungCoDau_DocRaNull_KhongPhaiEngineMacDinh()
    {
        // Điền một engine "mặc định" cho answer cũ là khẳng định điều ta không đo được — đúng lớp
        // lỗi mà bản vá F11 (`?? 0` biến khuyết thành số 0) đã phải đi bịt.
        using var t = new TestDb();
        var (_, answerId) = await SeedScoringAsync(t, transcript: "co transcript nhung khong co dau");

        var saved = await t.Db.PracticeAnswers.AsNoTracking().FirstAsync(a => a.Id == answerId);
        Assert.NotNull(saved.Transcript);
        Assert.Null(saved.TranscriptEngine);
    }

    // ── (5) INT-3: thu âm lại → dấu reset cùng transcript ───────────────────────

    [Fact]
    public async Task UploadLai_XoaConDauCuaBanGhiCu()
    {
        // Bản ghi âm cũ không còn tồn tại; giữ dấu lại là khai lai lịch cho một bản chép đã bị thay,
        // và dấu đó sẽ được đọc như thể mô tả bản chép MỚI. Cùng lý do F11 phải xoá cụm chỉ số.
        using var t = new TestDb();
        var candidate = Guid.NewGuid();
        var session = TestDb.Session(candidate, SessionStatus.InProgress);
        var q = TestDb.Question(session.Id);
        var crit = TestDb.Criterion(session.JobCategory);
        var answer = TestDb.Answer(session.Id, q.Id, AnswerStatus.Scored, DateTime.UtcNow, DateTime.UtcNow);
        answer.Transcript = "ban chep cu";
        answer.TranscriptEngine = RemoteEngine;
        t.Db.AddRange(session, q, crit, answer);
        await t.Db.SaveChangesAsync();

        await Build(t, out _).UploadAnswerAsync(
            session.Id, q.Id, candidate, new MemoryStream([9]), "audio/webm", 30);

        var saved = await t.Db.PracticeAnswers.AsNoTracking().FirstAsync(a => a.Id == answer.Id);
        Assert.Null(saved.Transcript);
        Assert.Null(saved.TranscriptEngine);
    }

    // ── (6) Đường THÍCH ỨNG: /decide-next chép → lưu dấu → đẩy vào ScoringJob ────

    [Fact]
    public async Task Adaptive_LuuConDauTuDecideNext_VaDayVaoScoringJob()
    {
        // Ở đường này /decide-next là lần chép DUY NHẤT (worker sau đó bỏ Whisper). Rơi dấu ở bất kỳ
        // mắt xích nào (lưu / publish) thì buổi thích ứng vĩnh viễn không có lai lịch bản chép trong
        // khi buổi tĩnh vẫn có — lệch âm thầm, không lỗi, không log.
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
                It.IsAny<AdaptiveDecisionRequest>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(new DecideNextResult(
                "end", null, "toi nghi vay", "r", null, RemoteEngine));

        var svc = Build(t, out var jobs, decider);
        await svc.UploadAnswerAsync(
            session.Id, q.Id, candidate, new MemoryStream([1]), "audio/webm", 40);

        var saved = await t.Db.PracticeAnswers.AsNoTracking().FirstAsync(a => a.SessionId == session.Id);
        Assert.Equal(RemoteEngine, saved.TranscriptEngine);

        var job = Assert.Single(jobs);
        Assert.Equal("toi nghi vay", job.Transcript);
        Assert.Equal(RemoteEngine, job.TranscriptEngine);   // worker bỏ Whisper ⇒ phải nhận dấu từ job
    }

    [Fact]
    public async Task Adaptive_AiServiceCuKhongGuiDau_DauLaNull_KhongPhaiDauCu()
    {
        // AIService bản cũ trả transcript mà không trả dấu. Đây là bản chép MỚI TOANH nên "không
        // biết" là câu trả lời đúng; giữ dấu nào đó ở đây là bịa lai lịch cho nó.
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
                It.IsAny<AdaptiveDecisionRequest>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(new DecideNextResult("end", null, "toi nghi vay", "r"));

        var svc = Build(t, out var jobs, decider);
        await svc.UploadAnswerAsync(
            session.Id, q.Id, candidate, new MemoryStream([1]), "audio/webm", 40);

        var saved = await t.Db.PracticeAnswers.AsNoTracking().FirstAsync(a => a.SessionId == session.Id);
        Assert.Equal("toi nghi vay", saved.Transcript);
        Assert.Null(saved.TranscriptEngine);
        Assert.Null(Assert.Single(jobs).TranscriptEngine);
    }

    [Fact]
    public async Task LuongTinh_ChuaChep_JobKhongMangDau()
    {
        // Đường tĩnh: chưa ai chép ⇒ job không mang dấu ⇒ worker tự chép rồi tự đóng dấu.
        // Gửi một chuỗi bịa ở đây thì worker sẽ echo lại nó và ta lưu một lai lịch sai.
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
        Assert.Null(job.TranscriptEngine);
    }

    // ── (7) Republisher cũng phải mang dấu ──────────────────────────────────────

    [Fact]
    public async Task Republisher_MangConDauTheoJob()
    {
        // Answer nào phải cứu bằng republisher mà mất dấu, trong khi answer chấm trơn tru vẫn có ⇒
        // dữ liệu lệch đúng ở nhóm answer đã từng có sự cố. F11 đã dính đúng chỗ này (projection
        // `.Select` quên cột), nên khoá riêng.
        using var t = new TestDb();
        var session = TestDb.Session(Guid.NewGuid(), SessionStatus.InProgress);
        var q = TestDb.Question(session.Id);
        var a = TestDb.Answer(session.Id, q.Id, AnswerStatus.Uploaded,
            DateTime.UtcNow.AddMinutes(-10), lastPublished: null);
        a.Transcript = "da chep dong bo";
        a.TranscriptEngine = RemoteEngine;
        t.Db.AddRange(session, q, a, TestDb.Criterion(session.JobCategory));
        await t.Db.SaveChangesAsync();

        var (r, pub) = BuildRepublisher(t);
        ScoringJob? published = null;
        pub.Setup(p => p.PublishAsync(It.IsAny<ScoringJob>(), It.IsAny<CancellationToken>()))
           .Callback<ScoringJob, CancellationToken>((j, _) => published = j)
           .Returns(Task.CompletedTask);

        await ScanOnce(r);

        Assert.NotNull(published);
        Assert.Equal("da chep dong bo", published!.Transcript);
        Assert.Equal(RemoteEngine, published.TranscriptEngine);
    }

    [Fact]
    public async Task Republisher_AnswerChuaChep_JobKhongMangDau()
    {
        // Vế ÂM: không có gì để mang thì không được bịa ra.
        using var t = new TestDb();
        var session = TestDb.Session(Guid.NewGuid(), SessionStatus.InProgress);
        var q = TestDb.Question(session.Id);
        var a = TestDb.Answer(session.Id, q.Id, AnswerStatus.Uploaded,
            DateTime.UtcNow.AddMinutes(-10), lastPublished: null);
        t.Db.AddRange(session, q, a, TestDb.Criterion(session.JobCategory));
        await t.Db.SaveChangesAsync();

        var (r, pub) = BuildRepublisher(t);
        ScoringJob? published = null;
        pub.Setup(p => p.PublishAsync(It.IsAny<ScoringJob>(), It.IsAny<CancellationToken>()))
           .Callback<ScoringJob, CancellationToken>((j, _) => published = j)
           .Returns(Task.CompletedTask);

        await ScanOnce(r);

        Assert.NotNull(published);
        Assert.Null(published!.TranscriptEngine);
    }

    // ── Hạ tầng republisher (mượn mẫu StuckAnswerRepublisherTests) ──────────────
    private static async Task ScanOnce(StuckAnswerRepublisher r)
    {
        var mi = typeof(StuckAnswerRepublisher)
            .GetMethod("ScanOnceAsync", BindingFlags.NonPublic | BindingFlags.Instance)!;
        await (Task)mi.Invoke(r, [CancellationToken.None])!;
    }

    private static (StuckAnswerRepublisher r, Mock<IScoringJobPublisher> pub) BuildRepublisher(TestDb t)
    {
        var services = new ServiceCollection();
        services.AddDbContext<InterviewDbContext>(o =>
            o.UseSqlite(t.Connection).UseSnakeCaseNamingConvention());
        var provider = services.BuildServiceProvider();

        var pub = new Mock<IScoringJobPublisher>();
        var r = new StuckAnswerRepublisher(
            provider.GetRequiredService<IServiceScopeFactory>(),
            pub.Object,
            Options.Create(new RepublisherSettings { BatchSize = 200 }),
            Options.Create(new ScoringOptions()),
            NullLogger<StuckAnswerRepublisher>.Instance);
        return (r, pub);
    }
}
