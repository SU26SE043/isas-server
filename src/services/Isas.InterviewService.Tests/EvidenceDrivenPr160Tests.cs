using Isas.InterviewService.DTOs;
using Isas.InterviewService.Entities;
using Isas.InterviewService.Enums;
using Isas.InterviewService.Models;
using Isas.InterviewService.Services;
using Isas.InterviewService.Services.Interfaces;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using Moq;

namespace Isas.InterviewService.Tests;

/// <summary>
/// Evidence-driven interviewer (PR #160) — vòng đời evidence + hợp đồng seniority + endpoint SC3.
///
/// <para>Trước vòng này bộ test có <b>0</b> test cho vòng đời evidence: không có gì khoá việc khởi tạo,
/// việc ghi sau quyết định, hay việc evidence KHÔNG được phép cắt ngắn buổi.</para>
/// </summary>
public class EvidenceDrivenPr160Tests
{
    // ───────────────────────── scaffolding ─────────────────────────

    private static AnswerService BuildAdaptive(TestDb t, Mock<IAiServiceInterviewDecider> decider)
    {
        var publisher = new Mock<IScoringJobPublisher>();
        publisher.Setup(p => p.PublishAsync(It.IsAny<ScoringJob>(), It.IsAny<CancellationToken>()))
            .Returns(Task.CompletedTask);

        var storage = new Mock<IStorageService>();
        storage.Setup(s => s.UploadAsync(
                It.IsAny<Stream>(), It.IsAny<string>(), It.IsAny<Guid>(),
                It.IsAny<Guid>(), It.IsAny<string>(), It.IsAny<string>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync("answer-audio/seed.webm");

        return new AnswerService(
            t.Db, storage.Object, publisher.Object,
            new Mock<ISessionScoringNotifier>().Object, TestDb.ScoringOpts(),
            NullLogger<AnswerService>.Instance, decider.Object,
            Options.Create(new AdaptiveOptions { MaxFailuresPerSession = 3 }));
    }

    private static Mock<IAiServiceInterviewDecider> Decider(DecideNextResult result)
    {
        var d = new Mock<IAiServiceInterviewDecider>();
        d.Setup(x => x.DecideNextAsync(It.IsAny<AdaptiveDecisionRequest>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(result);
        return d;
    }

    private static PracticeSession ChainSession(Guid candidate, int maxDeep = 3, int maxQuestions = 20)
    {
        var s = TestDb.Session(candidate, SessionStatus.Ready);
        s.AdaptiveEnabled = true;
        s.MaxQuestions = maxQuestions;
        s.MaxFollowUps = 0;
        s.MaxDeepPerQuestion = maxDeep;
        return s;
    }

    private static PracticeQuestion Seed(Guid sessionId, int orderNo, string content = "Câu gốc")
        => new()
        {
            Id = Guid.NewGuid(), SessionId = sessionId, OrderNo = orderNo, Content = content,
            TimeLimitSec = 120, Kind = QuestionKind.Seed, Depth = 0, RootQuestionId = null
        };

    private static SessionCriterionEvidence Evidence(
        Guid sessionId, Guid criterionId, string name, string state, int deepCount = 0)
        => new()
        {
            SessionId = sessionId, CriterionId = criterionId, CriterionName = name,
            State = state, DeepCount = deepCount
        };

    private static Task<UploadAnswerResult> UploadAsync(
        AnswerService svc, Guid sessionId, Guid questionId, Guid candidate)
    {
        using var audio = new MemoryStream(new byte[] { 1 });
        return svc.UploadAnswerAsync(sessionId, questionId, candidate, audio, "audio/webm", 30);
    }

    private static PracticeService BuildPractice(
        TestDb t, AdaptiveOptions adaptive, Mock<IAiServiceQuestionGenerator>? generator = null)
    {
        generator ??= new Mock<IAiServiceQuestionGenerator>();
        // Đủ CẢ 3 overload không-grounded: rubric rỗng + không focusCriteria + không questionCount rơi
        // xuống overload 4 tham số, không mock thì ném "AIService không trả về câu hỏi nào".
        generator.Setup(g => g.GenerateQuestionsAsync(
                It.IsAny<string>(), It.IsAny<string?>(), It.IsAny<string?>(), It.IsAny<string>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(Enumerable.Range(1, 5).Select(i => new GeneratedQuestion { Content = $"Q{i}" }).ToList());
        generator.Setup(g => g.GenerateQuestionsAsync(
                It.IsAny<string>(), It.IsAny<string?>(), It.IsAny<string?>(),
                It.IsAny<IReadOnlyList<string>?>(), It.IsAny<int?>(), It.IsAny<string>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(Enumerable.Range(1, 5).Select(i => new GeneratedQuestion { Content = $"Q{i}" }).ToList());
        generator.Setup(g => g.GenerateQuestionsAsync(
                It.IsAny<string>(), It.IsAny<string?>(), It.IsAny<string?>(),
                It.IsAny<IReadOnlyList<string>?>(), It.IsAny<int?>(),
                It.IsAny<IReadOnlyList<GroundingChunk>?>(), It.IsAny<string>(),
                It.IsAny<IReadOnlyList<QuestionTargetCriterionDto>>(), It.IsAny<string>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(new GeneratedQuestionsResult(
                Enumerable.Range(1, 5).Select(i => new GeneratedQuestion { Content = $"Q{i}" }).ToList(),
                Array.Empty<QuestionCitationDto>()));

        var reservation = new Mock<ICreditReservationClient>();
        reservation.Setup(r => r.ReserveAsync(
                It.IsAny<string>(), It.IsAny<Guid>(), It.IsAny<Guid>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(new CreditReservationResult(Guid.NewGuid(), 1));

        return new PracticeService(
            t.Db, new Mock<IStorageService>().Object, generator.Object,
            new Mock<ISessionScoringNotifier>().Object, reservation.Object,
            NullLogger<PracticeService>.Instance, Options.Create(adaptive));
    }

    private static RubricCriterion Content(JobCategory cat, string name, string language = "vi")
    {
        var c = TestDb.Criterion(cat, name: name, language: language);
        c.ScoringScope = ScoringScope.WhenTargeted;
        return c;
    }

    // ═════════════════ 1. QUYẾT ĐỊNH SẢN PHẨM — evidence KHÔNG cắt ngắn buổi ═════════════════

    /// <summary>
    /// TEST QUAN TRỌNG NHẤT VÒNG NÀY. Buổi có ≥2 tiêu chí <c>FAILED</c> VẪN chạy hết ngân sách.
    ///
    /// <para>Luật cũ "2 criterion FAILED → end" cắt buổi giữa chừng: ứng viên đã trả 1 credit cho số câu
    /// họ chọn, và người bị cắt lại đúng là người trả lời kém ⇒ phạt hai lần. B2B thì số câu phụ thuộc
    /// chất lượng trả lời sẽ phá CAMP-10 (điểm vẫn đem xếp hạng chung).</para>
    /// </summary>
    [Fact]
    public async Task HaiTieuChiFailed_VanChayHetNganSach_KhongCatBuoi()
    {
        using var t = new TestDb();
        var candidate = Guid.NewGuid();
        var session = ChainSession(candidate);
        var root = Seed(session.Id, 1);
        var c1 = TestDb.Criterion(session.JobCategory, name: "Tiêu chí 1");
        var c2 = TestDb.Criterion(session.JobCategory, name: "Tiêu chí 2");
        t.Db.AddRange(session, root, c1, c2);
        await t.Db.SaveChangesAsync();
        // Hai tiêu chí ĐỀU đã FAILED trước lượt này — đúng điều kiện luật cũ dùng để đóng buổi.
        t.Db.AddRange(
            Evidence(session.Id, c1.Id, c1.Name, "FAILED"),
            Evidence(session.Id, c2.Id, c2.Name, "FAILED"));
        await t.Db.SaveChangesAsync();

        var decider = Decider(new DecideNextResult("follow_up", "Hỏi tiếp", "ts", null));
        var res = await UploadAsync(BuildAdaptive(t, decider), session.Id, root.Id, candidate);

        // AI VẪN được hỏi (luật cũ `return EndOutcome` trước cả lời gọi này).
        decider.Verify(x => x.DecideNextAsync(
            It.IsAny<AdaptiveDecisionRequest>(), It.IsAny<CancellationToken>()), Times.Once);
        Assert.Equal("follow_up", res.NextAction);
        Assert.Equal("Hỏi tiếp", res.NextQuestion?.Content);
        Assert.False(res.InterviewComplete);
        Assert.Equal(2, await t.Db.PracticeQuestions.CountAsync(q => q.SessionId == session.Id));
    }

    /// <summary>
    /// Vế còn lại của luật cũ: MỌI tiêu chí đã terminal (SATISFIED/FAILED) cũng KHÔNG được đóng buổi.
    /// </summary>
    [Fact]
    public async Task MoiTieuChiTerminal_VanChayTiep_KhongDongBuoi()
    {
        using var t = new TestDb();
        var candidate = Guid.NewGuid();
        var session = ChainSession(candidate);
        var root = Seed(session.Id, 1);
        var c1 = TestDb.Criterion(session.JobCategory, name: "Tiêu chí 1");
        var c2 = TestDb.Criterion(session.JobCategory, name: "Tiêu chí 2");
        t.Db.AddRange(session, root, c1, c2);
        await t.Db.SaveChangesAsync();
        t.Db.AddRange(
            Evidence(session.Id, c1.Id, c1.Name, "SATISFIED"),
            Evidence(session.Id, c2.Id, c2.Name, "SATISFIED"));
        await t.Db.SaveChangesAsync();

        var decider = Decider(new DecideNextResult("clarify", "Làm rõ thêm", "ts", null));
        var res = await UploadAsync(BuildAdaptive(t, decider), session.Id, root.Id, candidate);

        decider.Verify(x => x.DecideNextAsync(
            It.IsAny<AdaptiveDecisionRequest>(), It.IsAny<CancellationToken>()), Times.Once);
        Assert.Equal("Làm rõ thêm", res.NextQuestion?.Content);
    }

    /// <summary>
    /// Chế độ FRONTIER (kill-switch <c>MaxDeepPerQuestion = 0</c>) là nơi luật cũ gây hại rõ nhất: tới
    /// được đó nghĩa là <c>pendingCount == 0</c> ⇒ <c>InterviewComplete: true</c> ⇒ FE báo "đã hỏi xong,
    /// mời nộp bài" dù ngân sách còn. Nay vẫn hỏi tiếp.
    /// </summary>
    [Fact]
    public async Task Frontier_HaiTieuChiFailed_KhongBaoHoanTat()
    {
        using var t = new TestDb();
        var candidate = Guid.NewGuid();
        var session = ChainSession(candidate, maxDeep: 0);   // frontier
        var root = Seed(session.Id, 1);
        var c1 = TestDb.Criterion(session.JobCategory, name: "Tiêu chí 1");
        var c2 = TestDb.Criterion(session.JobCategory, name: "Tiêu chí 2");
        t.Db.AddRange(session, root, c1, c2);
        await t.Db.SaveChangesAsync();
        t.Db.AddRange(
            Evidence(session.Id, c1.Id, c1.Name, "FAILED"),
            Evidence(session.Id, c2.Id, c2.Name, "FAILED"));
        await t.Db.SaveChangesAsync();

        var decider = Decider(new DecideNextResult("new_question", "Câu mới", "ts", null));
        var res = await UploadAsync(BuildAdaptive(t, decider), session.Id, root.Id, candidate);

        Assert.False(res.InterviewComplete);
        Assert.Equal("Câu mới", res.NextQuestion?.Content);
    }

    // ═════════════════ 2. GHI evidence sau quyết định ═════════════════

    [Fact]
    public async Task GhiEvidence_SauQuyetDinh_LuuState_Found_Missing()
    {
        using var t = new TestDb();
        var candidate = Guid.NewGuid();
        var session = ChainSession(candidate);
        var root = Seed(session.Id, 1);
        var c1 = TestDb.Criterion(session.JobCategory, name: "Tiêu chí 1");
        t.Db.AddRange(session, root, c1);
        await t.Db.SaveChangesAsync();
        t.Db.Add(Evidence(session.Id, c1.Id, c1.Name, "UNKNOWN"));
        await t.Db.SaveChangesAsync();

        var svc = BuildAdaptive(t, Decider(new DecideNextResult(
            "follow_up", "Hỏi sâu", "ts", null,
            TargetCriterionId: c1.Id.ToString(),
            EvidenceFound: ["đã nêu index", "  "],       // chuỗi trắng bị lọc
            MissingEvidence: ["chưa nói về khoá ngoại"],
            NewEvidenceState: "PARTIAL")));
        await UploadAsync(svc, session.Id, root.Id, candidate);

        var ev = await t.Db.SessionCriterionEvidence.AsNoTracking()
            .SingleAsync(e => e.SessionId == session.Id && e.CriterionId == c1.Id);
        Assert.Equal("PARTIAL", ev.State);
        Assert.Equal(["đã nêu index"], ev.EvidenceFound);
        Assert.Equal(["chưa nói về khoá ngoại"], ev.MissingEvidence);
    }

    /// <summary>
    /// <c>DeepCount</c> CỘNG DỒN theo tiêu chí, KHÔNG gán <c>question.Depth</c>.
    ///
    /// <para><c>Depth</c> là độ sâu trong CHUỖI đào sâu của một câu GỐC (INT-17b) — sai đại lượng; và vì
    /// là phép GÁN nên một decision đến từ câu gốc (<c>Depth == 0</c>) sẽ RESET bộ đếm về 0. Ở đây lượt 2
    /// đến từ câu đào sâu depth 1, lượt 3 lại đến từ một câu GỐC khác (depth 0): dưới hành vi cũ kết quả
    /// cuối là <b>0</b>; đúng phải là <b>3</b>.</para>
    /// </summary>
    [Fact]
    public async Task DeepCount_CongDonTheoTieuChi_KhongBiCauGocResetVe0()
    {
        using var t = new TestDb();
        var candidate = Guid.NewGuid();
        var session = ChainSession(candidate);
        var root1 = Seed(session.Id, 1, "Gốc 1");
        var root2 = Seed(session.Id, 5, "Gốc 2");
        var c1 = TestDb.Criterion(session.JobCategory, name: "Tiêu chí 1");
        t.Db.AddRange(session, root1, root2, c1);
        await t.Db.SaveChangesAsync();
        t.Db.Add(Evidence(session.Id, c1.Id, c1.Name, "UNKNOWN"));
        await t.Db.SaveChangesAsync();

        var svc = BuildAdaptive(t, Decider(new DecideNextResult(
            "follow_up", "Hỏi sâu", "ts", null,
            TargetCriterionId: c1.Id.ToString(), NewEvidenceState: "PARTIAL")));

        await UploadAsync(svc, session.Id, root1.Id, candidate);        // decision từ câu gốc (Depth 0)
        var child = await t.Db.PracticeQuestions.AsNoTracking()
            .Where(q => q.SessionId == session.Id && q.Depth == 1).SingleAsync();
        await UploadAsync(svc, session.Id, child.Id, candidate);        // decision từ câu sâu (Depth 1)
        await UploadAsync(svc, session.Id, root2.Id, candidate);        // lại từ câu GỐC (Depth 0)

        var ev = await t.Db.SessionCriterionEvidence.AsNoTracking()
            .SingleAsync(e => e.SessionId == session.Id && e.CriterionId == c1.Id);
        Assert.Equal(3, ev.DeepCount);
    }

    [Fact]
    public async Task TargetCriterionId_KhongParseDuoc_BoQua_KhongNem()
    {
        using var t = new TestDb();
        var candidate = Guid.NewGuid();
        var session = ChainSession(candidate);
        var root = Seed(session.Id, 1);
        var c1 = TestDb.Criterion(session.JobCategory, name: "Tiêu chí 1");
        t.Db.AddRange(session, root, c1);
        await t.Db.SaveChangesAsync();
        t.Db.Add(Evidence(session.Id, c1.Id, c1.Name, "UNKNOWN"));
        await t.Db.SaveChangesAsync();

        var svc = BuildAdaptive(t, Decider(new DecideNextResult(
            "follow_up", "Hỏi sâu", "ts", null,
            TargetCriterionId: "không-phải-guid", NewEvidenceState: "SATISFIED")));
        var res = await UploadAsync(svc, session.Id, root.Id, candidate);

        // Answer/câu kế KHÔNG được hỏng vì evidence lỗi (ứng viên đã trả credit cho buổi này).
        Assert.Equal("Hỏi sâu", res.NextQuestion?.Content);
        var ev = await t.Db.SessionCriterionEvidence.AsNoTracking()
            .SingleAsync(e => e.SessionId == session.Id);
        Assert.Equal("UNKNOWN", ev.State);
    }

    [Fact]
    public async Task NewEvidenceState_La_BoQua()
    {
        using var t = new TestDb();
        var candidate = Guid.NewGuid();
        var session = ChainSession(candidate);
        var root = Seed(session.Id, 1);
        var c1 = TestDb.Criterion(session.JobCategory, name: "Tiêu chí 1");
        t.Db.AddRange(session, root, c1);
        await t.Db.SaveChangesAsync();
        t.Db.Add(Evidence(session.Id, c1.Id, c1.Name, "UNKNOWN"));
        await t.Db.SaveChangesAsync();

        var svc = BuildAdaptive(t, Decider(new DecideNextResult(
            "follow_up", "Hỏi sâu", "ts", null,
            TargetCriterionId: c1.Id.ToString(), NewEvidenceState: "MAYBE")));
        await UploadAsync(svc, session.Id, root.Id, candidate);

        var ev = await t.Db.SessionCriterionEvidence.AsNoTracking().SingleAsync();
        Assert.Equal("UNKNOWN", ev.State);
        Assert.Equal(0, ev.DeepCount);
    }

    /// <summary>Criterion hợp lệ nhưng KHÔNG thuộc snapshot của buổi → bỏ qua, không tạo row mới.</summary>
    [Fact]
    public async Task CriterionNgoaiSnapshot_BoQua_KhongTaoRowMoi()
    {
        using var t = new TestDb();
        var candidate = Guid.NewGuid();
        var session = ChainSession(candidate);
        var root = Seed(session.Id, 1);
        var inScope = TestDb.Criterion(session.JobCategory, name: "Trong snapshot");
        var outOfScope = TestDb.Criterion(session.JobCategory, name: "Ngoài snapshot");
        t.Db.AddRange(session, root, inScope, outOfScope);
        await t.Db.SaveChangesAsync();
        t.Db.Add(Evidence(session.Id, inScope.Id, inScope.Name, "UNKNOWN"));
        await t.Db.SaveChangesAsync();

        var svc = BuildAdaptive(t, Decider(new DecideNextResult(
            "follow_up", "Hỏi sâu", "ts", null,
            TargetCriterionId: outOfScope.Id.ToString(), NewEvidenceState: "SATISFIED")));
        await UploadAsync(svc, session.Id, root.Id, candidate);

        Assert.Equal(1, await t.Db.SessionCriterionEvidence.CountAsync(e => e.SessionId == session.Id));
        Assert.Equal("UNKNOWN", (await t.Db.SessionCriterionEvidence.AsNoTracking().SingleAsync()).State);
    }

    // ═════════════════ 3. CHECK constraint ở tầng DB ═════════════════

    /// <summary>
    /// State lạ bị CHECK chặn. Guard C# ở AnswerService là lớp một; CHECK là lớp hai cho MỌI đường ghi —
    /// và nó là lớp duy nhất bắt được lỗi kiểu S11 (<c>varchar(16)</c> vs giá trị dài hơn: SQLite không
    /// enforce độ dài nên test xanh 100% trong khi Postgres vỡ).
    /// </summary>
    [Fact]
    public async Task EvidenceState_La_ViPhamCheck()
    {
        using var t = new TestDb();
        var candidate = Guid.NewGuid();
        var session = TestDb.Session(candidate, SessionStatus.Ready);
        var c1 = TestDb.Criterion(session.JobCategory);
        t.Db.AddRange(session, c1);
        await t.Db.SaveChangesAsync();

        t.Db.Add(Evidence(session.Id, c1.Id, c1.Name, "MAYBE"));

        await Assert.ThrowsAsync<DbUpdateException>(() => t.Db.SaveChangesAsync());
    }

    [Theory]
    [InlineData("UNKNOWN")]
    [InlineData("PARTIAL")]
    [InlineData("SATISFIED")]
    [InlineData("FAILED")]
    public async Task EvidenceState_HopLe_QuaDuocCheck(string state)
    {
        using var t = new TestDb();
        var candidate = Guid.NewGuid();
        var session = TestDb.Session(candidate, SessionStatus.Ready);
        var c1 = TestDb.Criterion(session.JobCategory);
        t.Db.AddRange(session, c1);
        await t.Db.SaveChangesAsync();

        t.Db.Add(Evidence(session.Id, c1.Id, c1.Name, state));
        await t.Db.SaveChangesAsync();

        Assert.Equal(state, (await t.Db.SessionCriterionEvidence.AsNoTracking().SingleAsync()).State);
    }

    // ═════════════════ 4. Khởi tạo evidence ═════════════════

    [Fact]
    public async Task InitB2C_AdaptiveBat_TaoRowChoMoiTieuChiNoiDung()
    {
        using var t = new TestDb();
        var candidate = Guid.NewGuid();
        t.Db.AddRange(
            Content(JobCategory.BE, "Nội dung 1"),
            Content(JobCategory.BE, "Nội dung 2"),
            TestDb.Criterion(JobCategory.BE, name: "Cách nói"));   // Always → KHÔNG vào evidence
        await t.Db.SaveChangesAsync();

        var svc = BuildPractice(t, new AdaptiveOptions
        {
            Enabled = true, SeedCount = 5, MaxQuestions = 20, MaxDeepPerQuestion = 3
        });
        var res = await svc.CreateSessionAsync(
            candidate, new CreatePracticeSessionRequest(null, null, JobCategory.BE));

        var evidence = await t.Db.SessionCriterionEvidence.AsNoTracking()
            .Where(e => e.SessionId == res.Id).OrderBy(e => e.CriterionName).ToListAsync();
        Assert.Equal(["Nội dung 1", "Nội dung 2"], evidence.Select(e => e.CriterionName));
        Assert.All(evidence, e => Assert.Equal("UNKNOWN", e.State));
    }

    /// <summary>
    /// Adaptive TẮT → KHÔNG khởi tạo evidence. Evidence chỉ được đọc/ghi ở đường thích ứng, mà
    /// <c>GetSessionAsync</c> lại TRẢ nó ra API ⇒ khởi tạo cho buổi tĩnh là hiện "chưa có bằng chứng cho
    /// mọi tiêu chí" trên một buổi mà cơ chế đó không hề chạy.
    /// </summary>
    [Fact]
    public async Task InitB2C_AdaptiveTat_KhongTaoRowNao()
    {
        using var t = new TestDb();
        var candidate = Guid.NewGuid();
        t.Db.AddRange(Content(JobCategory.BE, "Nội dung 1"), Content(JobCategory.BE, "Nội dung 2"));
        await t.Db.SaveChangesAsync();

        var svc = BuildPractice(t, new AdaptiveOptions { Enabled = false });
        var res = await svc.CreateSessionAsync(
            candidate, new CreatePracticeSessionRequest(null, null, JobCategory.BE));

        Assert.Empty(await t.Db.SessionCriterionEvidence.AsNoTracking()
            .Where(e => e.SessionId == res.Id).ToListAsync());
    }

    [Fact]
    public async Task InitB2B_AdaptiveBat_SnapshotToanBoRubricCampaign()
    {
        using var t = new TestDb();
        var candidate = Guid.NewGuid();
        var svc = BuildPractice(t, new AdaptiveOptions { Enabled = true });

        var res = await svc.CreateCampaignSessionAsync(candidate, new CreateCampaignSessionRequest(
            CampaignId: Guid.NewGuid(), OrgId: Guid.NewGuid(), JobCategory: JobCategory.BE,
            Questions: ["Câu 1", "Câu 2"],
            Criteria: [new CampaignCriterionInput("HR gõ 1", null, 0.5m, 5),
                       new CampaignCriterionInput("HR gõ 2", null, 0.5m, 5)],
            AdaptiveEnabled: true, MaxDeepPerQuestion: 2));

        var evidence = await t.Db.SessionCriterionEvidence.AsNoTracking()
            .Where(e => e.SessionId == res.Id).OrderBy(e => e.CriterionName).ToListAsync();
        // Tiêu chí campaign do HR gõ nhận DEFAULT `Always` (SC2) ⇒ lọc WhenTargeted bên B2B sẽ ra RỖNG.
        // Vì thế B2B CỐ Ý snapshot cả rubric — khác B2C về cơ chế, giống nhau về ý nghĩa.
        Assert.Equal(["HR gõ 1", "HR gõ 2"], evidence.Select(e => e.CriterionName));
    }

    [Fact]
    public async Task InitB2B_AdaptiveTat_KhongTaoRowNao()
    {
        using var t = new TestDb();
        var candidate = Guid.NewGuid();
        var svc = BuildPractice(t, new AdaptiveOptions { Enabled = true });

        var res = await svc.CreateCampaignSessionAsync(candidate, new CreateCampaignSessionRequest(
            CampaignId: Guid.NewGuid(), OrgId: Guid.NewGuid(), JobCategory: JobCategory.BE,
            Questions: ["Câu 1"],
            Criteria: [new CampaignCriterionInput("HR gõ 1", null, 1.0m, 5)],
            AdaptiveEnabled: false));

        Assert.Empty(await t.Db.SessionCriterionEvidence.AsNoTracking()
            .Where(e => e.SessionId == res.Id).ToListAsync());
    }

    // ═════════════════ 5. Endpoint SC3 khớp đường tạo session ═════════════════

    /// <summary>
    /// Endpoint khớp đường tạo session ở ca mà bộ test cũ KHÔNG thể phát hiện drift:
    /// <c>language = "en"</c> VÀ <c>criteriaCount > 0</c>.
    ///
    /// <para>Test SC3 cũ chạy cả hai đường ở <c>criteriaCount == 0</c> + <c>vi</c>: số tiêu chí nội dung
    /// là SÀN của số câu gốc, mà sàn 0 thì không nâng gì cả ⇒ hardcode <c>"vi"</c> ở endpoint không thể
    /// biểu hiện. Ở đây rubric <c>en</c> có 4 tiêu chí nội dung còn rubric <c>vi</c> chỉ có 1: đọc nhầm
    /// ngôn ngữ sẽ ra 2 câu gốc thay vì 4.</para>
    /// </summary>
    [Fact]
    public async Task SessionOptions_Language_En_KhopSoCauGocCuaBuoiThat()
    {
        using var t = new TestDb();
        var candidate = Guid.NewGuid();
        // Con số CỐ Ý chọn để PHÂN BIỆT ĐƯỢC, không chỉ "khác nhau": với maxQuestions=20/maxDeep=3/
        // SeedCount=5 thì byBudget = 5, nên sàn-theo-tiêu-chí chỉ BIND khi > 5. EN có 7 ⇒ seeds = 7;
        // VI có 1 ⇒ seeds = 5. Nếu endpoint đọc nhầm sang rubric vi thì preview ra 5 còn buổi thật ra 7
        // ⇒ assert cuối ĐỎ. Chọn EN = 4 sẽ cho cả hai cùng ra 5 và test "xanh" một cách vô nghĩa.
        t.Db.AddRange(
            Content(JobCategory.BE, "EN 1", "en"), Content(JobCategory.BE, "EN 2", "en"),
            Content(JobCategory.BE, "EN 3", "en"), Content(JobCategory.BE, "EN 4", "en"),
            Content(JobCategory.BE, "EN 5", "en"), Content(JobCategory.BE, "EN 6", "en"),
            Content(JobCategory.BE, "EN 7", "en"),
            Content(JobCategory.BE, "VI 1"));
        await t.Db.SaveChangesAsync();

        int? requestedCount = null;
        var generator = new Mock<IAiServiceQuestionGenerator>();
        generator.Setup(g => g.GenerateQuestionsAsync(
                It.IsAny<string>(), It.IsAny<string?>(), It.IsAny<string?>(),
                It.IsAny<IReadOnlyList<string>?>(), It.IsAny<int?>(),
                It.IsAny<IReadOnlyList<GroundingChunk>?>(), It.IsAny<string>(),
                It.IsAny<IReadOnlyList<QuestionTargetCriterionDto>>(), It.IsAny<string>(), It.IsAny<CancellationToken>()))
            .Callback((string _, string? _, string? _, IReadOnlyList<string>? _, int? count,
                       IReadOnlyList<GroundingChunk>? _, string _,
                       IReadOnlyList<QuestionTargetCriterionDto> _, string _, CancellationToken _) => requestedCount = count)
            .ReturnsAsync(new GeneratedQuestionsResult(
                Enumerable.Range(1, 7).Select(i => new GeneratedQuestion { Content = $"Q{i}" }).ToList(),
                Array.Empty<QuestionCitationDto>()));

        // ⚠ KHÔNG đi qua BuildPractice ở đây: nó `Setup` LẠI đúng overload này và ghi đè `.Callback`
        // bên trên ⇒ `requestedCount` mãi null và test "đo" một thứ không tồn tại.
        // Bilingual gate: PracticeService đọc `Interview:Bilingual:Enabled` từ IConfiguration.
        var svc = BilingualPractice(t, generator);

        var options = await svc.GetSessionOptionsAsync(candidate, "BE", "en");
        await svc.CreateSessionAsync(candidate, new CreatePracticeSessionRequest(
            null, null, JobCategory.BE, QuestionCount: 20, Language: "en"));

        var preview = options.Preview.Single(p => p.QuestionCount == 20);
        Assert.Equal(7, options.ContentCriteriaCount);     // rubric EN, KHÔNG phải rubric vi (1)
        Assert.Equal(7, requestedCount);                   // đường tạo session thật xin 7 câu gốc
        Assert.Equal(preview.SeedCount, requestedCount);   // endpoint == đường tạo session thật
    }

    private static PracticeService BilingualPractice(TestDb t, Mock<IAiServiceQuestionGenerator> generator)
    {
        var reservation = new Mock<ICreditReservationClient>();
        reservation.Setup(r => r.ReserveAsync(
                It.IsAny<string>(), It.IsAny<Guid>(), It.IsAny<Guid>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(new CreditReservationResult(Guid.NewGuid(), 1));
        var config = new ConfigurationBuilder().AddInMemoryCollection(new Dictionary<string, string?>
        {
            ["Interview:Bilingual:Enabled"] = "true"
        }).Build();

        return new PracticeService(
            t.Db, new Mock<IStorageService>().Object, generator.Object,
            new Mock<ISessionScoringNotifier>().Object, reservation.Object,
            NullLogger<PracticeService>.Instance,
            Options.Create(new AdaptiveOptions
            {
                Enabled = true, SeedCount = 5, MaxQuestions = 20, MaxDeepPerQuestion = 3
            }),
            config: config);
    }

    /// <summary>
    /// Trần báo cho UI == trần dùng để TỪ CHỐI. Trước đây endpoint báo max = trần gói/config nhưng
    /// <c>ValidateQuestionCount</c> chỉ chặn 1..20 ⇒ API tự mâu thuẫn với chính nó.
    /// </summary>
    [Fact]
    public async Task QuestionCount_VuotTranThat_Bi400_DuVanDuoi20()
    {
        using var t = new TestDb();
        var candidate = Guid.NewGuid();
        var svc = BuildPractice(t, new AdaptiveOptions
        {
            Enabled = true, SeedCount = 5, MaxQuestions = 8, MaxDeepPerQuestion = 3
        });

        var options = await svc.GetSessionOptionsAsync(candidate, "BE");
        Assert.Equal(8, options.QuestionCountMax);

        var ex = await Assert.ThrowsAsync<InvalidOperationException>(() =>
            svc.CreateSessionAsync(candidate, new CreatePracticeSessionRequest(
                null, null, JobCategory.BE, QuestionCount: 20)));
        Assert.Contains("8", ex.Message);
        Assert.Empty(await t.Db.PracticeSessions.AsNoTracking().ToListAsync());
    }

    /// <summary>Preset sập trùng nhau khi trần thấp → dedupe (UI không hiện 2 nút y hệt).</summary>
    [Fact]
    public async Task Presets_TranThap_KhongTraNutTrungNhau()
    {
        using var t = new TestDb();
        var svc = BuildPractice(t, new AdaptiveOptions
        {
            Enabled = true, SeedCount = 5, MaxQuestions = 8, MaxDeepPerQuestion = 3
        });

        var options = await svc.GetSessionOptionsAsync(Guid.NewGuid(), "BE");

        Assert.Equal(
            options.Presets.Select(p => p.QuestionCount).Distinct().Count(),
            options.Presets.Count);
        Assert.Equal("long", options.Presets.Last().Key);   // giá trị lớn nhất giữ nhãn lớn nhất
    }

    /// <summary><c>CoversAllCriteria</c> phải tính THẬT kể cả khi adaptive tắt.</summary>
    [Fact]
    public async Task CoversAllCriteria_AdaptiveTat_VanTinhThat_KhongPhaiTrueVoDieuKien()
    {
        using var t = new TestDb();
        t.Db.AddRange(
            Content(JobCategory.BE, "Nội dung 1"), Content(JobCategory.BE, "Nội dung 2"),
            Content(JobCategory.BE, "Nội dung 3"), Content(JobCategory.BE, "Nội dung 4"),
            Content(JobCategory.BE, "Nội dung 5"), Content(JobCategory.BE, "Nội dung 6"),
            Content(JobCategory.BE, "Nội dung 7"));
        await t.Db.SaveChangesAsync();

        var svc = BuildPractice(t, new AdaptiveOptions { Enabled = false });
        var options = await svc.GetSessionOptionsAsync(Guid.NewGuid(), "BE");

        Assert.False(options.AdaptiveEnabled);
        Assert.Equal(7, options.ContentCriteriaCount);
        // preset "short" = 6 câu < 7 tiêu chí ⇒ KHÔNG phủ hết. Hành vi cũ trả `true` vô điều kiện.
        Assert.False(options.Presets.Single(p => p.QuestionCount == 6).CoversAllCriteria);
        Assert.True(options.Presets.Single(p => p.QuestionCount == 20).CoversAllCriteria);
    }

    // ═════════════════ 6. Hợp đồng seniority ═════════════════

    [Theory]
    [InlineData("junior")]      // sai HOA/thường — so case-sensitive, khớp CHECK ở DB
    [InlineData("SENIOR")]
    [InlineData("")]            // rỗng = giá trị SAI, KHÔNG âm thầm reset về Junior
    [InlineData("   ")]         // rỗng sau Trim() — cùng ca
    [InlineData("Lead")]
    public async Task Seniority_KhongHopLe_Nem400_TruocReserve(string seniority)
    {
        using var t = new TestDb();
        var reservation = new Mock<ICreditReservationClient>();
        var svc = new PracticeService(
            t.Db, new Mock<IStorageService>().Object, new Mock<IAiServiceQuestionGenerator>().Object,
            new Mock<ISessionScoringNotifier>().Object, reservation.Object,
            NullLogger<PracticeService>.Instance, Options.Create(new AdaptiveOptions()));

        await Assert.ThrowsAsync<InvalidOperationException>(() =>
            svc.CreateSessionAsync(Guid.NewGuid(),
                new CreatePracticeSessionRequest(null, null, JobCategory.BE, Seniority: seniority)));

        // PAY-5 — guard chạy TRƯỚC reserve ⇒ input sai KHÔNG giữ credit oan.
        reservation.Verify(r => r.ReserveAsync(
            It.IsAny<string>(), It.IsAny<Guid>(), It.IsAny<Guid>(), It.IsAny<CancellationToken>()),
            Times.Never);
    }

    [Theory]
    [InlineData(null, "Junior")]        // client cũ không gửi → mặc định
    [InlineData("Fresher", "Fresher")]
    [InlineData("Senior", "Senior")]
    [InlineData("  Middle  ", "Middle")]   // trim rồi mới so
    public async Task Seniority_HopLe_DongDauXuongDb(string? sent, string expected)
    {
        using var t = new TestDb();
        var svc = BuildPractice(t, new AdaptiveOptions { Enabled = false });

        var res = await svc.CreateSessionAsync(Guid.NewGuid(),
            new CreatePracticeSessionRequest(null, null, JobCategory.BE, Seniority: sent));

        Assert.Equal(expected, res.Seniority);
        var stored = await t.Db.PracticeSessions.AsNoTracking().SingleAsync(s => s.Id == res.Id);
        Assert.Equal(expected, stored.Seniority);
    }

    /// <summary>
    /// Buổi luyện theo LỘ TRÌNH phải mang seniority của roadmap, không rơi vào mặc định "Junior".
    ///
    /// <para>Test này sinh ra vì mutation "trả roadmap lesson về hardcode Junior" chạy qua XANH: grep cả
    /// bộ test cho <c>StartLessonAsync</c> ra RỖNG ⇒ đường này chưa từng được phủ, chứ không phải fix
    /// thừa. Không vô hại: seniority đi vào <c>/decide-next</c> (câu đào sâu hỏi sai tầm) và lộ ra
    /// <c>PracticeSessionResponse.Seniority</c> cho FE.</para>
    /// </summary>
    [Fact]
    public async Task RoadmapLesson_MangSeniorityCuaRoadmap_KhongPhaiJunior()
    {
        using var t = new TestDb();
        var candidate = Guid.NewGuid();
        var lesson = new RoadmapLesson
        {
            Id = Guid.NewGuid(), OrderNo = 1, Title = "Lesson 1", Status = LessonStatus.Theory
        };
        t.Db.Roadmaps.Add(new Roadmap
        {
            Id = Guid.NewGuid(), CandidateId = candidate, JobCategory = JobCategory.BE,
            Level = RoadmapLevel.Senior,          // ← nguồn sự thật; hành vi cũ bỏ qua nó
            Status = RoadmapStatus.Active, CreatedAt = DateTime.UtcNow,
            Milestones =
            [
                new RoadmapMilestone
                {
                    Id = Guid.NewGuid(), OrderNo = 1, Title = "Milestone 1",
                    FocusCriteria = ["Clarity"], Status = MilestoneStatus.Pending,
                    Lessons = [lesson]
                }
            ]
        });
        await t.Db.SaveChangesAsync();
        var roadmap = await t.Db.Roadmaps.AsNoTracking().SingleAsync();

        CreatePracticeSessionRequest? captured = null;
        var practice = new Mock<IPracticeService>();
        practice.Setup(p => p.CreateLessonSessionAsync(
                It.IsAny<Guid>(), It.IsAny<CreatePracticeSessionRequest>(), It.IsAny<Guid>(),
                It.IsAny<IReadOnlyList<string>?>(), It.IsAny<LessonContext?>(),
                It.IsAny<CancellationToken>()))
            .Callback((Guid cid, CreatePracticeSessionRequest req, Guid sid,
                       IReadOnlyList<string>? _, LessonContext? _, CancellationToken _) =>
            {
                captured = req;
                // Phải tạo row session THẬT: link lesson sau đó chạy FK roadmap_lessons.session_id
                // (SQLite CÓ enforce FK trong EF10) — mock trả DTO suông sẽ nổ FK, không phải lỗi code.
                var s = TestDb.Session(cid, SessionStatus.Ready);
                s.Id = sid;
                s.Seniority = req.Seniority ?? "Junior";
                t.Db.PracticeSessions.Add(s);
                t.Db.SaveChanges();
            })
            .ReturnsAsync(new PracticeSessionResponse(
                Guid.NewGuid(), "Ready", "BE", "vi", null, null, DateTime.UtcNow, null, []));

        var svc = new RoadmapLessonService(
            t.Db, practice.Object, new Mock<IAiServiceRoadmapGenerator>().Object,
            NullLogger<RoadmapLessonService>.Instance);

        await svc.StartLessonAsync(candidate, roadmap.Id, lesson.Id);

        Assert.NotNull(captured);
        Assert.Equal("Senior", captured!.Seniority);
    }

    [Fact]
    public async Task Seniority_B2B_CungHopDong()
    {
        using var t = new TestDb();
        var svc = BuildPractice(t, new AdaptiveOptions { Enabled = false });

        await Assert.ThrowsAsync<InvalidOperationException>(() =>
            svc.CreateCampaignSessionAsync(Guid.NewGuid(), new CreateCampaignSessionRequest(
                CampaignId: Guid.NewGuid(), OrgId: Guid.NewGuid(), JobCategory: JobCategory.BE,
                Questions: ["Câu 1"], Criteria: [new CampaignCriterionInput("C", null, 1.0m, 5)],
                Seniority: "   ")));

        var ok = await svc.CreateCampaignSessionAsync(Guid.NewGuid(), new CreateCampaignSessionRequest(
            CampaignId: Guid.NewGuid(), OrgId: Guid.NewGuid(), JobCategory: JobCategory.BE,
            Questions: ["Câu 1"], Criteria: [new CampaignCriterionInput("C", null, 1.0m, 5)],
            Seniority: null));
        Assert.Equal("Junior", ok.Seniority);
    }
}
