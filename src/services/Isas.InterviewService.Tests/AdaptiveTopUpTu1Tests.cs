using Isas.InterviewService.DTOs;
using Isas.InterviewService.Entities;
using Isas.InterviewService.Enums;
using Isas.InterviewService.Models;
using Isas.InterviewService.Services;
using Isas.InterviewService.Services.Interfaces;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using Moq;

namespace Isas.InterviewService.Tests;

/// <summary>
/// TU1 — BÙ CÂU GỐC khi chuỗi hết sớm mà ngân sách buổi vẫn còn · DUP1 — chốt chặn chống câu trùng.
///
/// <para><b>Số đo prod</b> (chỉ buổi chạy trọn, <c>status='Scored'</c>) — buổi thích ứng giao ÍT CÂU
/// HƠN số ứng viên đã chọn và đã trả credit:</para>
/// <code>
/// chọn | max_deep | số buổi | câu thực tế TB | ít nhất | số buổi thiếu
///   20 |        3 |       6 |            9,5 |       6 |          6/6
///    5 |        3 |      17 |            4,9 |       4 |         1/17
///    5 |        0 |      10 |            3,6 |       2 |         7/10
///    6 |        0 |       9 |            2,9 |       1 |          9/9
///    4 |        3 |       8 |            3,6 |       2 |          2/8
/// </code>
/// <para>Chọn 20 nhận về 9,5 — chưa tới một nửa. F2b: ứng viên trả 1 credit cho ĐÚNG số câu họ chọn.</para>
///
/// <para>DUP1 có bằng chứng riêng: prod có 10 buổi chứa câu trùng khít từng chữ; một buổi có câu
/// Clarify depth 1 và depth 2 GIỐNG NHAU TỪNG KÝ TỰ, cả hai nhận cùng một bản chép của ứng viên.</para>
/// </summary>
public class AdaptiveTopUpTu1Tests
{
    // ── Hạ tầng test ────────────────────────────────────────────────────────────────────────────

    /// Bắt log để assert. Chốt chặn DUP1 và trần bù chỉ biểu hiện QUA LOG — không assert log thì gỡ
    /// sạch cảnh báo đi test vẫn xanh, mà im lặng ở đây đúng là hình dạng lỗi cần tránh.
    private sealed class LogRecorder : ILogger<AnswerService>
    {
        public List<string> Warnings { get; } = [];
        public List<string> Infos { get; } = [];
        public IDisposable? BeginScope<TState>(TState state) where TState : notnull => null;
        public bool IsEnabled(LogLevel logLevel) => true;
        public void Log<TState>(
            LogLevel logLevel, EventId eventId, TState state, Exception? exception,
            Func<TState, Exception?, string> formatter)
        {
            if (logLevel == LogLevel.Warning) Warnings.Add(formatter(state, exception));
            if (logLevel == LogLevel.Information) Infos.Add(formatter(state, exception));
        }
    }

    /// Ghi lại đúng những tham số TU1 quyết định: chủ đề nhắm (focusCriteria), số câu xin, tập tiêu chí
    /// nội dung gửi để gắn nhãn.
    private sealed class GenCall
    {
        public IReadOnlyList<string>? FocusCriteria { get; set; }
        public int? Count { get; set; }
        public IReadOnlyList<QuestionTargetCriterionDto>? Criteria { get; set; }
        public string? Language { get; set; }
        public string? Seniority { get; set; }
        public string? CvText { get; set; }
        public int Calls { get; set; }
    }

    private static Mock<IAiServiceQuestionGenerator> Generator(
        Func<List<GeneratedQuestion>> questions, GenCall? capture = null)
    {
        var gen = new Mock<IAiServiceQuestionGenerator>();
        gen.Setup(g => g.GenerateQuestionsAsync(
                It.IsAny<string>(), It.IsAny<string?>(), It.IsAny<string?>(),
                It.IsAny<IReadOnlyList<string>?>(), It.IsAny<int?>(),
                It.IsAny<IReadOnlyList<GroundingChunk>?>(), It.IsAny<string>(),
                It.IsAny<IReadOnlyList<QuestionTargetCriterionDto>?>(), It.IsAny<string>(),
                It.IsAny<CancellationToken>()))
            .Callback((string _, string? cv, string? _, IReadOnlyList<string>? focus, int? count,
                       IReadOnlyList<GroundingChunk>? _, string lang,
                       IReadOnlyList<QuestionTargetCriterionDto>? criteria, string sen, CancellationToken _) =>
            {
                if (capture is null) return;
                capture.FocusCriteria = focus;
                capture.Count = count;
                capture.Criteria = criteria;
                capture.Language = lang;
                capture.Seniority = sen;
                capture.CvText = cv;
                capture.Calls++;
            })
            .Returns(() => Task.FromResult(
                new GeneratedQuestionsResult(questions(), Array.Empty<QuestionCitationDto>())));
        return gen;
    }

    private static Mock<IAiServiceQuestionGenerator> ThrowingGenerator()
    {
        var gen = new Mock<IAiServiceQuestionGenerator>();
        gen.Setup(g => g.GenerateQuestionsAsync(
                It.IsAny<string>(), It.IsAny<string?>(), It.IsAny<string?>(),
                It.IsAny<IReadOnlyList<string>?>(), It.IsAny<int?>(),
                It.IsAny<IReadOnlyList<GroundingChunk>?>(), It.IsAny<string>(),
                It.IsAny<IReadOnlyList<QuestionTargetCriterionDto>?>(), It.IsAny<string>(),
                It.IsAny<CancellationToken>()))
            .ThrowsAsync(new AiServiceException("AIService /generate-questions sập"));
        return gen;
    }

    private static List<GeneratedQuestion> One(string content, IReadOnlyList<Guid>? targets = null)
        => [new GeneratedQuestion { Content = content, TargetCriterionIds = targets }];

    private static Mock<IAiServiceInterviewDecider> Decider(DecideNextResult result)
    {
        var d = new Mock<IAiServiceInterviewDecider>();
        d.Setup(x => x.DecideNextAsync(
                It.IsAny<AdaptiveDecisionRequest>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(result);
        return d;
    }

    private static AnswerService Build(
        TestDb t,
        Mock<IAiServiceInterviewDecider> decider,
        Mock<IAiServiceQuestionGenerator>? generator = null,
        AdaptiveOptions? adaptive = null,
        ILogger<AnswerService>? logger = null)
    {
        var publisher = new Mock<IScoringJobPublisher>();
        publisher
            .Setup(p => p.PublishAsync(It.IsAny<ScoringJob>(), It.IsAny<CancellationToken>()))
            .Returns(Task.CompletedTask);

        var storage = new Mock<IStorageService>();
        storage
            .Setup(s => s.UploadAsync(
                It.IsAny<Stream>(), It.IsAny<string>(), It.IsAny<Guid>(),
                It.IsAny<Guid>(), It.IsAny<string>(), It.IsAny<string>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync("answer-audio/seed.webm");

        return new AnswerService(
            t.Db, storage.Object, publisher.Object,
            new Mock<ISessionScoringNotifier>().Object, TestDb.ScoringOpts(),
            logger ?? NullLogger<AnswerService>.Instance, decider.Object,
            Options.Create(adaptive ?? new AdaptiveOptions()),
            generator?.Object);
    }

    /// Buổi B2C chế độ CHUỖI. `MaxFollowUps = 0` = trần buổi TẮT (xem AdaptiveOptions).
    private static PracticeSession ChainSession(
        Guid candidate, Guid? campaignId = null, int maxDeep = 3, int maxQuestions = 20)
    {
        var s = TestDb.Session(candidate, SessionStatus.Ready, campaignId: campaignId);
        s.AdaptiveEnabled = true;
        s.MaxQuestions = maxQuestions;
        s.MaxFollowUps = 0;
        s.MaxDeepPerQuestion = maxDeep;
        return s;
    }

    private static PracticeQuestion Seed(Guid sessionId, int orderNo, string content = "Câu gốc")
        => new()
        {
            Id = Guid.NewGuid(),
            SessionId = sessionId,
            OrderNo = orderNo,
            Content = content,
            TimeLimitSec = 120,
            Kind = QuestionKind.Seed,
            Depth = 0,
            RootQuestionId = null
        };

    private static async Task<UploadAnswerResult> UploadAsync(
        AnswerService svc, Guid sessionId, Guid questionId, Guid candidate)
    {
        using var audio = new MemoryStream(new byte[] { 1 });
        return await svc.UploadAnswerAsync(sessionId, questionId, candidate, audio, "audio/webm", 30);
    }

    private static RubricCriterion ContentCriterion(PracticeSession session, string name)
        => new()
        {
            Id = Guid.NewGuid(), Name = name, Weight = 0.2m, MaxScore = 5,
            IsActive = true, JobCategory = session.JobCategory, Language = session.Language,
            ScoringScope = ScoringScope.WhenTargeted, Version = 1
        };

    /// `session_criterion_evidence.criterion_id` là FK → `rubric_criteria` (Restrict), nên mọi dòng
    /// bằng chứng phải đi kèm một tiêu chí THẬT — đúng như snapshot được gieo lúc tạo buổi.
    private static SessionCriterionEvidence AddEvidence(
        TestDb t, PracticeSession session, string name, string state, int deepCount = 0)
    {
        var criterion = ContentCriterion(session, name);
        var evidence = new SessionCriterionEvidence
        {
            SessionId = session.Id,
            CriterionId = criterion.Id,
            CriterionName = name,
            State = state,
            DeepCount = deepCount
        };
        t.Db.AddRange(criterion, evidence);
        return evidence;
    }

    // ── TU1 (a) — bù đúng khi còn ngân sách và hết câu gốc chưa trả lời ─────────────────────────

    /// Ca CHÍNH: 1 câu gốc, ngân sách 20, AI trả `end` ⇒ trước TU1 buổi đóng ở đúng 1 câu (chính là
    /// hình dạng hàng "20 chọn → 9,5 giao" trong bảng đo). Sau TU1 phải có câu gốc BÙ.
    [Fact]
    public async Task ConNganSach_HetCauGocChuaTraLoi_BuMotCauGocMoi()
    {
        using var t = new TestDb();
        var candidate = Guid.NewGuid();
        var session = ChainSession(candidate);
        var root = Seed(session.Id, 1, "Gốc 1");
        t.Db.AddRange(session, root, TestDb.Criterion(session.JobCategory));
        await t.Db.SaveChangesAsync();

        var gen = Generator(() => One("Câu bù nhắm tiêu chí còn thiếu"));
        var res = await UploadAsync(
            Build(t, Decider(new DecideNextResult("end", null, "ts", null)), gen),
            session.Id, root.Id, candidate);

        var qs = await t.Db.PracticeQuestions.AsNoTracking()
            .Where(q => q.SessionId == session.Id).OrderBy(q => q.OrderNo).ToListAsync();
        Assert.Equal(2, qs.Count);

        var topUp = qs[1];
        Assert.Equal("Câu bù nhắm tiêu chí còn thiếu", topUp.Content);
        Assert.Equal(0, topUp.Depth);                     // câu GỐC — tự mọc chuỗi đào sâu của nó
        Assert.Null(topUp.RootQuestionId);
        Assert.Equal(QuestionKind.NewQuestion, topUp.Kind);
        Assert.Equal(res.AnswerId, topUp.GeneratedFromAnswerId);   // khoá idempotency (xem test riêng)
        // OrderNo bám LƯỚI câu gốc (stride = 1 + maxDeep = 4): 1 → 5, chừa khe 2..4 cho chuỗi của gốc 1.
        Assert.Equal(5, topUp.OrderNo);
        Assert.Equal(session.TimeLimitSec, topUp.TimeLimitSec);

        // Client nhận câu kế ngay trong response và KHÔNG bị báo "đã hỏi xong, mời nộp bài".
        Assert.False(res.InterviewComplete);
        Assert.Equal("new_question", res.NextAction);
        Assert.NotNull(res.NextQuestion);
        Assert.Equal(topUp.Id, res.NextQuestion!.Id);
    }

    /// Đường vào thứ hai: chuỗi CHẠM TRẦN ĐỘ SÂU (bước 1) — nhánh này return TRƯỚC cả khối gọi
    /// `/decide-next`, nên nếu chỉ gắn TU1 vào nhánh `endsChain` thì ca này im lặng không bù.
    [Fact]
    public async Task ChamTranDoSau_CungBu_VaKhongGoiDecider()
    {
        using var t = new TestDb();
        var candidate = Guid.NewGuid();
        var session = ChainSession(candidate, maxDeep: 1, maxQuestions: 6);
        var root = Seed(session.Id, 1, "Gốc 1");
        var child = new PracticeQuestion
        {
            Id = Guid.NewGuid(), SessionId = session.Id, OrderNo = 2, Content = "Sâu 1",
            TimeLimitSec = 120, Kind = QuestionKind.FollowUp, Depth = 1, RootQuestionId = root.Id
        };
        // Câu gốc đã trả lời rồi ⇒ trả lời nốt câu sâu 1 là hết câu chưa trả lời.
        var rootAnswer = TestDb.Answer(session.Id, root.Id, AnswerStatus.Scored, DateTime.UtcNow, null);
        t.Db.AddRange(session, root, child, rootAnswer, TestDb.Criterion(session.JobCategory));
        await t.Db.SaveChangesAsync();

        var decider = Decider(new DecideNextResult("follow_up", "không được hỏi", "ts", null));
        var gen = Generator(() => One("Câu bù sau khi chạm trần độ sâu"));
        var res = await UploadAsync(Build(t, decider, gen), session.Id, child.Id, candidate);

        decider.Verify(x => x.DecideNextAsync(
            It.IsAny<AdaptiveDecisionRequest>(), It.IsAny<CancellationToken>()), Times.Never);
        Assert.Equal(3, await t.Db.PracticeQuestions.CountAsync(q => q.SessionId == session.Id));
        Assert.False(res.InterviewComplete);

        var topUp = await t.Db.PracticeQuestions.AsNoTracking()
            .SingleAsync(q => q.SessionId == session.Id && q.Kind == QuestionKind.NewQuestion);
        // stride = 1 + 1 = 2, maxOrder = 2 ⇒ khe gốc kế là 3.
        Assert.Equal(3, topUp.OrderNo);
        Assert.Equal(0, topUp.Depth);
    }

    /// Câu bù là câu GỐC thật: nó được phép mọc chuỗi đào sâu của chính nó ở lượt sau.
    [Fact]
    public async Task CauBu_MocDuocChuoiDaoSauCuaChinhNo()
    {
        using var t = new TestDb();
        var candidate = Guid.NewGuid();
        var session = ChainSession(candidate);
        var root = Seed(session.Id, 1, "Gốc 1");
        t.Db.AddRange(session, root, TestDb.Criterion(session.JobCategory));
        await t.Db.SaveChangesAsync();

        var decider = new Mock<IAiServiceInterviewDecider>();
        var calls = 0;
        decider.Setup(x => x.DecideNextAsync(
                It.IsAny<AdaptiveDecisionRequest>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(() => calls++ == 0
                ? new DecideNextResult("end", null, "ts", null)              // đóng chuỗi gốc 1
                : new DecideNextResult("follow_up", "Đào sâu câu bù", "ts", null));

        var svc = Build(t, decider, Generator(() => One("Câu bù")));
        await UploadAsync(svc, session.Id, root.Id, candidate);

        var topUp = await t.Db.PracticeQuestions.AsNoTracking()
            .SingleAsync(q => q.SessionId == session.Id && q.Content == "Câu bù");
        await UploadAsync(svc, session.Id, topUp.Id, candidate);

        var child = await t.Db.PracticeQuestions.AsNoTracking()
            .SingleAsync(q => q.SessionId == session.Id && q.Content == "Đào sâu câu bù");
        Assert.Equal(1, child.Depth);
        Assert.Equal(topUp.Id, child.RootQuestionId);
        Assert.Equal(topUp.OrderNo + 1, child.OrderNo);   // rơi vào khe stride đã chừa cho câu bù
    }

    // ── TU1 (b) — B2B KHÔNG bù ─────────────────────────────────────────────────────────────────

    /// CAMP-10: B2B xếp hạng ứng viên chung một bảng nên số câu phải bằng nhau — chính vì vậy code
    /// hiện ép `MaxFollowUps = 0` cho B2B. Bù câu cho B2B phá công bằng theo kiểu KHÔNG ai thấy:
    /// điểm vẫn ra, chỉ là hai ứng viên được đo bằng số câu khác nhau.
    [Fact]
    public async Task B2B_KhongBaoGioBu()
    {
        using var t = new TestDb();
        var candidate = Guid.NewGuid();
        var campaignId = Guid.NewGuid();
        var session = ChainSession(candidate, campaignId: campaignId);
        var root = Seed(session.Id, 1);
        t.Db.AddRange(session, root, TestDb.Criterion(session.JobCategory, campaignId: campaignId));
        await t.Db.SaveChangesAsync();

        var gen = Generator(() => One("Câu bù KHÔNG được phép xuất hiện"));
        var res = await UploadAsync(
            Build(t, Decider(new DecideNextResult("end", null, "ts", null)), gen),
            session.Id, root.Id, candidate);

        Assert.Equal(1, await t.Db.PracticeQuestions.CountAsync(q => q.SessionId == session.Id));
        gen.Verify(g => g.GenerateQuestionsAsync(
            It.IsAny<string>(), It.IsAny<string?>(), It.IsAny<string?>(),
            It.IsAny<IReadOnlyList<string>?>(), It.IsAny<int?>(),
            It.IsAny<IReadOnlyList<GroundingChunk>?>(), It.IsAny<string>(),
            It.IsAny<IReadOnlyList<QuestionTargetCriterionDto>?>(), It.IsAny<string>(),
            It.IsAny<CancellationToken>()), Times.Never);
        Assert.True(res.InterviewComplete);   // hành vi cũ nguyên vẹn
    }

    // ── TU1 (c) — không bù quá ngân sách ────────────────────────────────────────────────────────

    /// `MaxQuestions` là TRẦN CỨNG: bù để giao ĐỦ thứ đã bán, không phải để giao thừa.
    [Fact]
    public async Task KhongBuVuotMaxQuestions()
    {
        using var t = new TestDb();
        var candidate = Guid.NewGuid();
        var session = ChainSession(candidate, maxQuestions: 2);
        var root = Seed(session.Id, 1, "Gốc 1");
        t.Db.AddRange(session, root, TestDb.Criterion(session.JobCategory));
        await t.Db.SaveChangesAsync();

        var svc = Build(t, Decider(new DecideNextResult("end", null, "ts", null)),
            Generator(() => One($"Câu bù {Guid.NewGuid()}")));

        await UploadAsync(svc, session.Id, root.Id, candidate);          // 1 → 2 câu
        var topUp = await t.Db.PracticeQuestions.AsNoTracking()
            .SingleAsync(q => q.SessionId == session.Id && q.Kind == QuestionKind.NewQuestion);
        var last = await UploadAsync(svc, session.Id, topUp.Id, candidate);   // đã chạm trần

        Assert.Equal(2, await t.Db.PracticeQuestions.CountAsync(q => q.SessionId == session.Id));
        Assert.True(last.InterviewComplete);
    }

    /// Buổi KHÔNG có trần cứng (`MaxQuestions = 0`) ⇒ không có "số câu đã bán" để đối chiếu, mà bù
    /// không trần thì thành phỏng vấn vô tận ⇒ không bù.
    [Fact]
    public async Task KhongTranCung_MaxQuestionsBang0_KhongBu()
    {
        using var t = new TestDb();
        var candidate = Guid.NewGuid();
        var session = ChainSession(candidate, maxQuestions: 0);
        var root = Seed(session.Id, 1);
        t.Db.AddRange(session, root, TestDb.Criterion(session.JobCategory));
        await t.Db.SaveChangesAsync();

        var res = await UploadAsync(
            Build(t, Decider(new DecideNextResult("end", null, "ts", null)),
                Generator(() => One("Câu bù"))),
            session.Id, root.Id, candidate);

        Assert.Equal(1, await t.Db.PracticeQuestions.CountAsync(q => q.SessionId == session.Id));
        Assert.True(res.InterviewComplete);
    }

    /// Trần SỐ LẦN bù mỗi buổi: một AI hỏng không được đốt hết ngân sách bằng câu vô dụng.
    [Fact]
    public async Task TranSoLanBu_MoiBuoi_ChamTranThiThoi_VaCoLog()
    {
        using var t = new TestDb();
        var candidate = Guid.NewGuid();
        var session = ChainSession(candidate, maxQuestions: 20);
        var root = Seed(session.Id, 1);
        t.Db.AddRange(session, root, TestDb.Criterion(session.JobCategory));
        await t.Db.SaveChangesAsync();

        var log = new LogRecorder();
        var svc = Build(t, Decider(new DecideNextResult("end", null, "ts", null)),
            Generator(() => One($"Câu bù {Guid.NewGuid()}")),
            new AdaptiveOptions { MaxTopUpsPerSession = 1 }, log);

        await UploadAsync(svc, session.Id, root.Id, candidate);
        var topUp = await t.Db.PracticeQuestions.AsNoTracking()
            .SingleAsync(q => q.SessionId == session.Id && q.Kind == QuestionKind.NewQuestion);
        var last = await UploadAsync(svc, session.Id, topUp.Id, candidate);

        Assert.Equal(2, await t.Db.PracticeQuestions.CountAsync(q => q.SessionId == session.Id));
        Assert.True(last.InterviewComplete);
        Assert.Contains(log.Warnings, w => w.Contains("TU1") && w.Contains("thôi bù"));
    }

    // ── TU1 (d) — lỗi sinh câu thì DEGRADE, không hỏng upload ───────────────────────────────────

    [Fact]
    public async Task SinhCauBuLoi_UploadVanThanhCong_VaVeHanhViCu()
    {
        using var t = new TestDb();
        var candidate = Guid.NewGuid();
        var session = ChainSession(candidate);
        var root = Seed(session.Id, 1);
        t.Db.AddRange(session, root, TestDb.Criterion(session.JobCategory));
        await t.Db.SaveChangesAsync();

        var log = new LogRecorder();
        var res = await UploadAsync(
            Build(t, Decider(new DecideNextResult("end", null, "ts", null)), ThrowingGenerator(),
                logger: log),
            session.Id, root.Id, candidate);

        // Answer vẫn lưu, upload vẫn trả kết quả, buổi đóng ĐÚNG như trước TU1.
        Assert.Equal(1, await t.Db.PracticeAnswers.CountAsync(a => a.SessionId == session.Id));
        Assert.Equal(1, await t.Db.PracticeQuestions.CountAsync(q => q.SessionId == session.Id));
        Assert.True(res.InterviewComplete);
        Assert.Equal("end", res.NextAction);
        Assert.Contains(log.Warnings, w => w.Contains("TU1") && w.Contains("bỏ bù"));

        // Cùng phanh với /decide-next: AIService hỏng thì bộ đếm lỗi của buổi phải nhích lên, nếu
        // không thì mỗi lượt upload vẫn chờ hết timeout của lời gọi AI thứ hai.
        var s = await t.Db.PracticeSessions.AsNoTracking().FirstAsync(x => x.Id == session.Id);
        Assert.Equal(1, s.AdaptiveFailures);
    }

    /// AI trả về bộ câu RỖNG (không ném) → không bù, không nổ, buổi đóng như cũ.
    [Fact]
    public async Task SinhCauBuTraRong_KhongBu_KhongNo()
    {
        using var t = new TestDb();
        var candidate = Guid.NewGuid();
        var session = ChainSession(candidate);
        var root = Seed(session.Id, 1);
        t.Db.AddRange(session, root, TestDb.Criterion(session.JobCategory));
        await t.Db.SaveChangesAsync();

        var res = await UploadAsync(
            Build(t, Decider(new DecideNextResult("end", null, "ts", null)),
                Generator(() => [])),
            session.Id, root.Id, candidate);

        Assert.Equal(1, await t.Db.PracticeQuestions.CountAsync(q => q.SessionId == session.Id));
        Assert.True(res.InterviewComplete);
    }

    // ── TU1 (e) — kill-switch ───────────────────────────────────────────────────────────────────

    [Fact]
    public async Task CoTat_TopUpRootQuestionsFalse_VeDungHanhViCu()
    {
        using var t = new TestDb();
        var candidate = Guid.NewGuid();
        var session = ChainSession(candidate);
        var root = Seed(session.Id, 1);
        t.Db.AddRange(session, root, TestDb.Criterion(session.JobCategory));
        await t.Db.SaveChangesAsync();

        var gen = Generator(() => One("Câu bù KHÔNG được phép xuất hiện"));
        var res = await UploadAsync(
            Build(t, Decider(new DecideNextResult("end", null, "ts", null)), gen,
                new AdaptiveOptions { TopUpRootQuestions = false }),
            session.Id, root.Id, candidate);

        Assert.Equal(1, await t.Db.PracticeQuestions.CountAsync(q => q.SessionId == session.Id));
        Assert.True(res.InterviewComplete);
        Assert.Equal("end", res.NextAction);
        gen.Verify(g => g.GenerateQuestionsAsync(
            It.IsAny<string>(), It.IsAny<string?>(), It.IsAny<string?>(),
            It.IsAny<IReadOnlyList<string>?>(), It.IsAny<int?>(),
            It.IsAny<IReadOnlyList<GroundingChunk>?>(), It.IsAny<string>(),
            It.IsAny<IReadOnlyList<QuestionTargetCriterionDto>?>(), It.IsAny<string>(),
            It.IsAny<CancellationToken>()), Times.Never);
    }

    /// Không đăng ký generator (test cũ / DI thiếu) → không bù, không nổ.
    [Fact]
    public async Task KhongCoGenerator_KhongBu_KhongNo()
    {
        using var t = new TestDb();
        var candidate = Guid.NewGuid();
        var session = ChainSession(candidate);
        var root = Seed(session.Id, 1);
        t.Db.AddRange(session, root, TestDb.Criterion(session.JobCategory));
        await t.Db.SaveChangesAsync();

        var res = await UploadAsync(
            Build(t, Decider(new DecideNextResult("end", null, "ts", null))),
            session.Id, root.Id, candidate);

        Assert.Equal(1, await t.Db.PracticeQuestions.CountAsync(q => q.SessionId == session.Id));
        Assert.True(res.InterviewComplete);
    }

    // ── TU1 (f) — IDEMPOTENCY: double-POST / re-upload không đẻ hai câu bù ──────────────────────

    /// Khoá idempotency của câu BÙ dùng lại đúng `generated_from_answer_id` + unique filtered index
    /// của chuỗi. Ca khó là đường CHẠM TRẦN ĐỘ SÂU: nó return TRƯỚC bước (2) "answer này đã đẻ con
    /// chưa", nên nếu TU1 không tự kiểm thì re-upload đẻ ra câu bù thứ hai.
    [Fact]
    public async Task ReUpload_DuongChamTranDoSau_KhongDeRaHaiCauBu()
    {
        using var t = new TestDb();
        var candidate = Guid.NewGuid();
        var session = ChainSession(candidate, maxDeep: 1, maxQuestions: 10);
        var root = Seed(session.Id, 1, "Gốc 1");
        var child = new PracticeQuestion
        {
            Id = Guid.NewGuid(), SessionId = session.Id, OrderNo = 2, Content = "Sâu 1",
            TimeLimitSec = 120, Kind = QuestionKind.FollowUp, Depth = 1, RootQuestionId = root.Id
        };
        var rootAnswer = TestDb.Answer(session.Id, root.Id, AnswerStatus.Scored, DateTime.UtcNow, null);
        t.Db.AddRange(session, root, child, rootAnswer, TestDb.Criterion(session.JobCategory));
        await t.Db.SaveChangesAsync();

        var gen = Generator(() => One($"Câu bù {Guid.NewGuid()}"));
        var svc = Build(t, Decider(new DecideNextResult("end", null, "ts", null)), gen);

        await UploadAsync(svc, session.Id, child.Id, candidate);
        await UploadAsync(svc, session.Id, child.Id, candidate);   // ghi đè cùng answer (INT-3)

        Assert.Equal(3, await t.Db.PracticeQuestions.CountAsync(q => q.SessionId == session.Id));
        gen.Verify(g => g.GenerateQuestionsAsync(
            It.IsAny<string>(), It.IsAny<string?>(), It.IsAny<string?>(),
            It.IsAny<IReadOnlyList<string>?>(), It.IsAny<int?>(),
            It.IsAny<IReadOnlyList<GroundingChunk>?>(), It.IsAny<string>(),
            It.IsAny<IReadOnlyList<QuestionTargetCriterionDto>?>(), It.IsAny<string>(),
            It.IsAny<CancellationToken>()), Times.Once);
    }

    // ── TU1 (g) — chọn chủ đề bù theo bằng chứng còn thiếu ──────────────────────────────────────

    /// `UNKNOWN` trước `PARTIAL`, và ưu tiên theo TRẠNG THÁI phải THẮNG `DeepCount`.
    /// ⚠ Không viết luật nào giả định phân bố phong phú: thực đo prod hiện là UNKNOWN 178 · PARTIAL 13
    /// · FAILED 5 · SATISFIED **0** (chưa tiêu chí nào từng đạt).
    [Fact]
    public async Task ChonChuDeBu_UuTienUNKNOWN_RoiPARTIAL()
    {
        using var t = new TestDb();
        var candidate = Guid.NewGuid();
        var session = ChainSession(candidate);
        var root = Seed(session.Id, 1);
        t.Db.AddRange(session, root, TestDb.Criterion(session.JobCategory));
        AddEvidence(t, session, "Đã đủ bằng chứng", "SATISFIED", deepCount: 0);
        AddEvidence(t, session, "Còn một nửa", "PARTIAL", deepCount: 0);
        AddEvidence(t, session, "Chưa hỏi bao giờ", "UNKNOWN", deepCount: 9);
        await t.Db.SaveChangesAsync();

        var call = new GenCall();
        await UploadAsync(
            Build(t, Decider(new DecideNextResult("end", null, "ts", null)),
                Generator(() => One("Câu bù"), call)),
            session.Id, root.Id, candidate);

        Assert.Equal(["Chưa hỏi bao giờ"], call.FocusCriteria);
        Assert.Equal(1, call.Count);                       // xin ĐÚNG 1 câu
        Assert.Equal(session.Language, call.Language);
        Assert.Equal(session.Seniority, call.Seniority);
    }

    /// Không còn tiêu chí nào thiếu bằng chứng ⇒ VẪN bù (ngân sách là thứ đã trả tiền, không phải
    /// phần thưởng), nhắm tiêu chí được đào sâu ÍT NHẤT.
    [Fact]
    public async Task HetTieuChiThieuBangChung_VanBu_NhamTieuChiDaoSauItNhat()
    {
        using var t = new TestDb();
        var candidate = Guid.NewGuid();
        var session = ChainSession(candidate);
        var root = Seed(session.Id, 1);
        t.Db.AddRange(session, root, TestDb.Criterion(session.JobCategory));
        AddEvidence(t, session, "Đã đào nhiều", "SATISFIED", deepCount: 5);
        AddEvidence(t, session, "Đào ít nhất", "SATISFIED", deepCount: 1);
        await t.Db.SaveChangesAsync();

        var call = new GenCall();
        await UploadAsync(
            Build(t, Decider(new DecideNextResult("end", null, "ts", null)),
                Generator(() => One("Câu bù"), call)),
            session.Id, root.Id, candidate);

        Assert.Equal(2, await t.Db.PracticeQuestions.CountAsync(q => q.SessionId == session.Id));
        Assert.Equal(["Đào ít nhất"], call.FocusCriteria);
    }

    // ── TU1 (g2) — SNAPSHOT BẰNG CHỨNG RỖNG là ĐA SỐ, không phải ca hiếm ───────────────────────
    //
    // Đo được 112/176 buổi adaptive KHÔNG có dòng `session_criterion_evidence` nào (64%). Snapshot
    // gieo từ `targetable`, mà biến đó rỗng đúng khi rubric riêng BC16 có toàn `ScoringScope = Always`
    // — chính lỗi SC2 (tương quan 94% trên 90 buổi: dùng rubric riêng ⇒ không có snapshot). SC2 được
    // vá ở chỗ khác, nhưng BUỔI CŨ rỗng VĨNH VIỄN ⇒ nhánh dự phòng không phải tạm thời.
    //
    // Nếu TU1 bám bảng bằng chứng mà không có dự phòng thì nó im lặng không chạy cho đúng nhóm buổi
    // đang thiếu câu nhất — tức bản vá trông như đã ship mà 64% buổi không hưởng gì.

    /// Snapshot RỖNG + rubric CÓ tiêu chí nội dung ⇒ vẫn bù, và chọn tiêu chí CHƯA câu nào nhắm tới
    /// (vừa lấp chỗ trống của điểm, vừa là cách tránh trùng chủ đề rẻ nhất).
    [Fact]
    public async Task SnapshotBangChungRong_VanBu_ChonTieuChiChuaCauNaoNham()
    {
        using var t = new TestDb();
        var candidate = Guid.NewGuid();
        var session = ChainSession(candidate);
        var daNham = new RubricCriterion
        {
            Id = Guid.NewGuid(), Name = "A — đã được hỏi", Weight = 0.2m, MaxScore = 5,
            IsActive = true, JobCategory = session.JobCategory, Language = "vi",
            ScoringScope = ScoringScope.WhenTargeted, Version = 1
        };
        var chuaNham = new RubricCriterion
        {
            Id = Guid.NewGuid(), Name = "B — chưa được hỏi", Weight = 0.2m, MaxScore = 5,
            IsActive = true, JobCategory = session.JobCategory, Language = "vi",
            ScoringScope = ScoringScope.WhenTargeted, Version = 1
        };
        var root = Seed(session.Id, 1);
        root.TargetCriterionIds = [daNham.Id];
        t.Db.AddRange(session, root, TestDb.Criterion(session.JobCategory), daNham, chuaNham);
        await t.Db.SaveChangesAsync();
        Assert.Equal(0, await t.Db.SessionCriterionEvidence.CountAsync());   // đúng ca SC2

        var call = new GenCall();
        var log = new LogRecorder();
        await UploadAsync(
            Build(t, Decider(new DecideNextResult("end", null, "ts", null)),
                Generator(() => One("Câu bù"), call), logger: log),
            session.Id, root.Id, candidate);

        Assert.Equal(2, await t.Db.PracticeQuestions.CountAsync(q => q.SessionId == session.Id));
        Assert.Equal(["B — chưa được hỏi"], call.FocusCriteria);
        // Log phải phân biệt hai đường: đọc log là biết ngay buổi nào đang chạy mù.
        Assert.Contains(log.Infos, i => i.Contains("TU1") && i.Contains("KHÔNG có snapshot bằng chứng"));
    }

    /// Ca xấu nhất và cũng là ca ĐÔNG NHẤT của SC2: snapshot rỗng VÀ rubric không có tiêu chí nội dung
    /// nào (rubric riêng BC16 toàn `Always`). Vẫn PHẢI bù — quyền lợi F2b không phụ thuộc bảng nội bộ
    /// nào có chạy hay không.
    [Fact]
    public async Task SnapshotRong_VaRubricKhongCoTieuChiNoiDung_VanBu_ChayMu()
    {
        using var t = new TestDb();
        var candidate = Guid.NewGuid();
        var session = ChainSession(candidate);
        var root = Seed(session.Id, 1);
        t.Db.AddRange(session, root, TestDb.Criterion(session.JobCategory));   // chỉ có tiêu chí `Always`
        await t.Db.SaveChangesAsync();

        var call = new GenCall();
        var log = new LogRecorder();
        await UploadAsync(
            Build(t, Decider(new DecideNextResult("end", null, "ts", null)),
                Generator(() => One("Câu bù"), call), logger: log),
            session.Id, root.Id, candidate);

        Assert.Equal(2, await t.Db.PracticeQuestions.CountAsync(q => q.SessionId == session.Id));
        Assert.Null(call.FocusCriteria);
        Assert.Null(call.Criteria);
        Assert.Contains(log.Infos, i => i.Contains("TU1") && i.Contains("bù mù"));
    }

    // ── TU1 (h) — nhãn tiêu chí của câu bù ──────────────────────────────────────────────────────

    /// Nhãn LẤY NGUYÊN từ AIService (đã qua guard `ParseTargets`). Câu bù có nhãn ⇒ chấm đúng phạm vi.
    [Fact]
    public async Task CauBu_GiuNguyenNhanTieuChiAiTraVe()
    {
        using var t = new TestDb();
        var candidate = Guid.NewGuid();
        var session = ChainSession(candidate);
        var root = Seed(session.Id, 1);
        var content = new RubricCriterion
        {
            Id = Guid.NewGuid(), Name = "Chiều sâu kỹ thuật", Weight = 0.2m, MaxScore = 5,
            IsActive = true, JobCategory = session.JobCategory, Language = "vi",
            ScoringScope = ScoringScope.WhenTargeted, Version = 1
        };
        t.Db.AddRange(session, root, TestDb.Criterion(session.JobCategory), content);
        await t.Db.SaveChangesAsync();

        var call = new GenCall();
        await UploadAsync(
            Build(t, Decider(new DecideNextResult("end", null, "ts", null)),
                Generator(() => One("Câu bù", [content.Id]), call)),
            session.Id, root.Id, candidate);

        var topUp = await t.Db.PracticeQuestions.AsNoTracking()
            .SingleAsync(q => q.SessionId == session.Id && q.Kind == QuestionKind.NewQuestion);
        Assert.Equal([content.Id], topUp.TargetCriterionIds);
        // Tiêu chí NỘI DUNG được gửi để AIService gắn nhãn (và để ParseTargets loại id lạ);
        // tiêu chí CÁCH NÓI (`Always`) thì không — chúng được chấm cho MỌI câu.
        Assert.NotNull(call.Criteria);
        Assert.Equal([content.Id], call.Criteria!.Select(c => c.CriterionId).ToArray());
    }

    /// AI KHÔNG gắn nhãn ⇒ giữ `null` = chấm ĐỦ rubric (lùi an toàn). CỐ Ý không tự gán tiêu chí ta
    /// vừa ĐỀ NGHỊ: đề nghị chủ đề khác hẳn với khẳng định "câu này đo tiêu chí đó", và tự khẳng
    /// định thay AI rồi thu hẹp phạm vi chấm theo nó là đúng lỗi chấm-theo-phạm-vi sinh ra để diệt.
    [Fact]
    public async Task CauBu_AiKhongGanNhan_GiuNull_KhongTuGanTieuChiDaDeNghi()
    {
        using var t = new TestDb();
        var candidate = Guid.NewGuid();
        var session = ChainSession(candidate);
        var root = Seed(session.Id, 1);
        t.Db.AddRange(session, root, TestDb.Criterion(session.JobCategory));
        AddEvidence(t, session, "Chiều sâu kỹ thuật", "UNKNOWN");
        await t.Db.SaveChangesAsync();

        await UploadAsync(
            Build(t, Decider(new DecideNextResult("end", null, "ts", null)),
                Generator(() => One("Câu bù"))),
            session.Id, root.Id, candidate);

        var topUp = await t.Db.PracticeQuestions.AsNoTracking()
            .SingleAsync(q => q.SessionId == session.Id && q.Kind == QuestionKind.NewQuestion);
        Assert.Null(topUp.TargetCriterionIds);
    }

    // ── DUP1 — chốt chặn chống câu trùng ────────────────────────────────────────────────────────

    /// Prod: một buổi có câu Clarify depth 1 và depth 2 GIỐNG NHAU TỪNG KÝ TỰ, cả hai nhận cùng một
    /// bản chép — ứng viên bị hỏi lại đúng câu vừa trả lời rồi bị chấm hai lần trên cùng một bài.
    [Fact]
    public async Task DaoSau_CauTrungKhitCauDaCo_KhongAppend_VaCoLogCanhBao()
    {
        using var t = new TestDb();
        var candidate = Guid.NewGuid();
        var session = ChainSession(candidate);
        var root = Seed(session.Id, 1, "Bạn xử lý race condition thế nào?");
        var other = Seed(session.Id, 5, "Gốc 2");   // giữ pendingCount > 0 để loại trừ nhánh bù
        t.Db.AddRange(session, root, other, TestDb.Criterion(session.JobCategory));
        await t.Db.SaveChangesAsync();

        var log = new LogRecorder();
        var res = await UploadAsync(
            Build(t, Decider(new DecideNextResult(
                "clarify", "Bạn xử lý race condition thế nào?", "ts", null)),
                logger: log),
            session.Id, root.Id, candidate);

        Assert.Equal(2, await t.Db.PracticeQuestions.CountAsync(q => q.SessionId == session.Id));
        Assert.Contains(log.Warnings, w => w.Contains("DUP1") && w.Contains("TRÙNG"));
        // Đóng chuỗi: còn Gốc 2 chưa trả lời ⇒ KHÔNG báo hoàn tất, KHÔNG trả action "end".
        Assert.False(res.InterviewComplete);
        Assert.Null(res.NextAction);
    }

    /// Chuẩn hoá NHẸ: trim · hoa/thường · khoảng trắng thừa. Đây là toàn bộ phạm vi — không fuzzy.
    [Fact]
    public async Task CauTrung_KhacHoaThuongVaKhoangTrangThua_VanBiChan()
    {
        using var t = new TestDb();
        var candidate = Guid.NewGuid();
        var session = ChainSession(candidate);
        var root = Seed(session.Id, 1, "Bạn xử lý race condition thế nào?");
        var other = Seed(session.Id, 5, "Gốc 2");
        t.Db.AddRange(session, root, other, TestDb.Criterion(session.JobCategory));
        await t.Db.SaveChangesAsync();

        await UploadAsync(
            Build(t, Decider(new DecideNextResult(
                "clarify", "  BẠN xử lý   RACE condition\tthế nào?  ", "ts", null))),
            session.Id, root.Id, candidate);

        Assert.Equal(2, await t.Db.PracticeQuestions.CountAsync(q => q.SessionId == session.Id));
    }

    /// Chặn phải HẸP: câu chỉ na ná (khác đúng một chữ) là câu hỏi HỢP LỆ — chặn nó là cắt bớt buổi
    /// của ứng viên đã trả tiền, đắt hơn hẳn việc để lọt một câu gần giống.
    [Fact]
    public async Task CauGanGiong_KhacMotChu_VanDuocAppend()
    {
        using var t = new TestDb();
        var candidate = Guid.NewGuid();
        var session = ChainSession(candidate);
        var root = Seed(session.Id, 1, "Bạn xử lý race condition thế nào?");
        var other = Seed(session.Id, 5, "Gốc 2");
        t.Db.AddRange(session, root, other, TestDb.Criterion(session.JobCategory));
        await t.Db.SaveChangesAsync();

        await UploadAsync(
            Build(t, Decider(new DecideNextResult(
                "clarify", "Bạn xử lý deadlock thế nào?", "ts", null))),
            session.Id, root.Id, candidate);

        Assert.Equal(3, await t.Db.PracticeQuestions.CountAsync(q => q.SessionId == session.Id));
    }

    /// Câu BÙ cũng đi qua đúng chốt đó — bù ra một câu đã hỏi thì bù cũng vô nghĩa.
    [Fact]
    public async Task CauBu_TrungCauDaCo_KhongAppend_VaCoLogCanhBao()
    {
        using var t = new TestDb();
        var candidate = Guid.NewGuid();
        var session = ChainSession(candidate);
        var root = Seed(session.Id, 1, "Bạn xử lý race condition thế nào?");
        t.Db.AddRange(session, root, TestDb.Criterion(session.JobCategory));
        await t.Db.SaveChangesAsync();

        var log = new LogRecorder();
        var res = await UploadAsync(
            Build(t, Decider(new DecideNextResult("end", null, "ts", null)),
                Generator(() => One("bạn XỬ LÝ race condition thế nào?")), logger: log),
            session.Id, root.Id, candidate);

        Assert.Equal(1, await t.Db.PracticeQuestions.CountAsync(q => q.SessionId == session.Id));
        Assert.Contains(log.Warnings, w => w.Contains("DUP1") && w.Contains("câu BÙ"));
        Assert.True(res.InterviewComplete);   // degrade về hành vi cũ
    }
}
