using System.Security.Claims;
using Isas.InterviewService.ApplicationDbContext;
using Isas.InterviewService.Controllers;
using Isas.InterviewService.DTOs;
using Isas.InterviewService.Entities;
using Isas.InterviewService.Enums;
using Isas.InterviewService.Services;
using Isas.InterviewService.Services.Interfaces;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging.Abstractions;
using Moq;

namespace Isas.InterviewService.Tests;

/// <summary>
/// BK16 — KHOÁ HỢP ĐỒNG resume phía Interview (D3 chỉ khoá phía Campaign).
///
/// Bối cảnh: D2 <c>GetOrCreateCampaignSessionAsync</c> idempotent theo (candidate, campaign) khi
/// session chưa terminal → ứng viên bấm "Start" nhiều lần (refresh/quay lại) phải nhận CÙNG session
/// đang mở, KHÔNG mất bài đã nộp và KHÔNG reset câu đã trả lời (INT-3: "1 answer/câu; upload lại =
/// ghi đè"; §State machine: resume chỉ làm câu CHƯA nộp, câu đã nộp giữ nguyên).
///
/// Kết luận sau khi đọc code (báo cáo BK16): KHÔNG có gap production — resume branch của
/// <c>GetOrCreateCampaignSessionAsync</c> và <c>GetSessionAsync</c> đều nạp answers (Include Scores)
/// và chỉ ĐỌC (không reset). Các test dưới khoá hợp đồng đó.
///
/// Mỗi "request" dựng service trên 1 context riêng (t.NewContext) để mô phỏng scope DbContext/request
/// thật — resume đọc từ DB, không dựa change-tracker in-memory.
/// </summary>
public class ResumeContractTests
{
    // ── builders (per-request context) ────────────────────────────────────
    private static PracticeService BuildPractice(InterviewDbContext db)
    {
        var notifier = new Mock<ISessionScoringNotifier>();
        notifier
            .Setup(n => n.NotifySessionScoredAsync(It.IsAny<Guid>(), It.IsAny<CancellationToken>()))
            .Returns(Task.CompletedTask);

        // BC2: reserve ví cá nhân chỉ ở nhánh B2C — nhánh B2B (campaign) không gọi, mock để đủ ctor.
        var reservation = new Mock<ICreditReservationClient>();
        reservation
            .Setup(r => r.ReserveAsync("User", It.IsAny<Guid>(), It.IsAny<Guid>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(new CreditReservationResult(Guid.NewGuid(), 1));

        return new PracticeService(
            db, new Mock<IStorageService>().Object,
            new Mock<IAiServiceQuestionGenerator>().Object,
            notifier.Object, reservation.Object,
            new Mock<ISessionEventPublisher>().Object,
            NullLogger<PracticeService>.Instance);
    }

    private static AnswerService BuildAnswer(InterviewDbContext db)
    {
        var storage = new Mock<IStorageService>();
        storage
            .Setup(s => s.UploadAsync(
                It.IsAny<Stream>(), It.IsAny<string>(), It.IsAny<Guid>(),
                It.IsAny<Guid>(), It.IsAny<string>(), It.IsAny<string>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync("answer-audio/seed.webm");

        var notifier = new Mock<ISessionScoringNotifier>();
        notifier
            .Setup(n => n.NotifySessionScoredAsync(It.IsAny<Guid>(), It.IsAny<CancellationToken>()))
            .Returns(Task.CompletedTask);

        // Publisher mock mặc định: PublishAsync trả completed Task → publish "thành công" → answer Scoring.
        return new AnswerService(
            db, storage.Object, new Mock<IScoringJobPublisher>().Object,
            notifier.Object, NullLogger<AnswerService>.Instance);
    }

    private static PracticeController BuildController(InterviewDbContext db, Guid candidateId)
    {
        var controller = new PracticeController(BuildPractice(db), NullLogger<PracticeController>.Instance);
        var principal = new ClaimsPrincipal(new ClaimsIdentity(
            [new Claim(ClaimTypes.NameIdentifier, candidateId.ToString())], "test"));
        controller.ControllerContext = new ControllerContext
        {
            HttpContext = new DefaultHttpContext { User = principal }
        };
        return controller;
    }

    // Request B2B 2 câu, 1 tiêu chí (materialize khi tạo mới; resume không đụng criteria).
    private static CreateCampaignSessionRequest Req(Guid campaignId) =>
        new(campaignId, JobCategory.BE,
            Questions: new[] { "Q1", "Q2" },
            Criteria: new[] { new CampaignCriterionInput("Communication", null, 1.0m, 5) });

    // Nộp 1 câu qua AnswerService (đường thật) trên context riêng; trả AnswerId.
    private static async Task<Guid> UploadAsync(TestDb t, Guid sessionId, Guid questionId, Guid candidate)
    {
        using var audio = new MemoryStream(new byte[] { 1, 2, 3 });
        var res = await BuildAnswer(t.NewContext())
            .UploadAnswerAsync(sessionId, questionId, candidate, audio, "audio/webm", 30);
        return res.AnswerId;
    }

    // ── (1) resume trả CÙNG session; câu đã nộp giữ nguyên, câu chưa nộp trống ─
    [Fact]
    public async Task Resume_TraCungSession_GiuAnswerDaNop_CauChuaNopTrong()
    {
        using var t = new TestDb();
        var candidate = Guid.NewGuid();
        var campaignId = Guid.NewGuid();
        var req = Req(campaignId);

        // "Start" lần đầu → tạo session B2B (Q1, Q2).
        var created = await BuildPractice(t.NewContext()).GetOrCreateCampaignSessionAsync(candidate, req);
        var q1 = created.Questions[0].Id;

        // Nộp câu 1 → session Ready→InProgress, answer câu 1 tồn tại.
        var answerId = await UploadAsync(t, created.Id, q1, candidate);

        // GetSession: câu 1 có answer, câu 2 trống.
        var got = await BuildPractice(t.NewContext()).GetSessionAsync(candidate, created.Id);
        Assert.NotNull(got);
        Assert.NotNull(got!.Questions[0].Answer);
        Assert.Equal(answerId, got.Questions[0].Answer!.Id);
        Assert.Null(got.Questions[1].Answer);

        // Resume (create-or-get lần 2) → CÙNG session; câu 1 GIỮ NGUYÊN answer cũ, câu 2 vẫn trống.
        var resumed = await BuildPractice(t.NewContext()).GetOrCreateCampaignSessionAsync(candidate, req);
        Assert.Equal(created.Id, resumed.Id);
        Assert.NotNull(resumed.Questions[0].Answer);
        Assert.Equal(answerId, resumed.Questions[0].Answer!.Id);   // không mất, không đẻ answer mới
        Assert.Null(resumed.Questions[1].Answer);

        // Resume chỉ ĐỌC → không hạ cấp trạng thái; đúng 1 session.
        await using var read = t.NewContext();
        var session = await read.PracticeSessions.AsNoTracking().SingleAsync(s => s.CampaignId == campaignId);
        Assert.Equal(SessionStatus.InProgress, session.Status);
    }

    // ── (2) resume nhiều lần KHÔNG nhân đôi/tạo lại answer row ────────────────
    [Fact]
    public async Task Resume_KhongTaoLaiHoacNhanDoiAnswerRow()
    {
        using var t = new TestDb();
        var candidate = Guid.NewGuid();
        var req = Req(Guid.NewGuid());

        var created = await BuildPractice(t.NewContext()).GetOrCreateCampaignSessionAsync(candidate, req);
        await UploadAsync(t, created.Id, created.Questions[0].Id, candidate);

        // Resume 2 lần nữa (idempotent create-or-get).
        await BuildPractice(t.NewContext()).GetOrCreateCampaignSessionAsync(candidate, req);
        await BuildPractice(t.NewContext()).GetOrCreateCampaignSessionAsync(candidate, req);

        await using var read = t.NewContext();
        Assert.Equal(1, await read.PracticeAnswers.CountAsync(a => a.SessionId == created.Id));
        Assert.Equal(1, await read.PracticeSessions.CountAsync(s => s.Id == created.Id));
    }

    // ── (3) INT-3: upload lại CÙNG câu = ghi đè idempotent (1 answer/câu) ──────
    [Fact]
    public async Task UploadLaiCungCau_GhiDe_Idempotent_MotAnswerMoiCau()
    {
        using var t = new TestDb();
        var candidate = Guid.NewGuid();
        var req = Req(Guid.NewGuid());

        var created = await BuildPractice(t.NewContext()).GetOrCreateCampaignSessionAsync(candidate, req);
        var q1 = created.Questions[0].Id;

        var firstId = await UploadAsync(t, created.Id, q1, candidate);

        // Giả lập đã có transcript từ lần chấm trước → chứng minh upload lại RESET transcript (publish lại).
        await using (var seed = t.NewContext())
        {
            await seed.PracticeAnswers.Where(a => a.Id == firstId)
                .ExecuteUpdateAsync(u => u.SetProperty(a => a.Transcript, "transcript cũ"));
        }

        // Upload lại cùng câu → ghi đè đúng answer cũ (INT-3): CÙNG AnswerId, không đẻ answer thứ 2.
        var secondId = await UploadAsync(t, created.Id, q1, candidate);
        Assert.Equal(firstId, secondId);

        await using var read = t.NewContext();
        Assert.Equal(1, await read.PracticeAnswers.CountAsync(a => a.SessionId == created.Id && a.QuestionId == q1));
        var ans = await read.PracticeAnswers.AsNoTracking().SingleAsync(a => a.Id == firstId);
        Assert.Null(ans.Transcript);   // ghi đè reset transcript để chấm lại
    }

    // ── (4) resume rồi làm nốt câu CHƯA nộp; câu đã nộp không đổi ─────────────
    [Fact]
    public async Task Resume_CauChuaNop_VanUploadDuoc_CauDaNopKhongDoi()
    {
        using var t = new TestDb();
        var candidate = Guid.NewGuid();
        var req = Req(Guid.NewGuid());

        var created = await BuildPractice(t.NewContext()).GetOrCreateCampaignSessionAsync(candidate, req);
        var q1 = created.Questions[0].Id;
        var q2 = created.Questions[1].Id;

        var a1 = await UploadAsync(t, created.Id, q1, candidate);

        // Resume rồi nộp câu 2 (chưa nộp) → thành công, tạo answer mới.
        var resumed = await BuildPractice(t.NewContext()).GetOrCreateCampaignSessionAsync(candidate, req);
        Assert.Equal(created.Id, resumed.Id);
        var a2 = await UploadAsync(t, created.Id, q2, candidate);

        Assert.NotEqual(a1, a2);
        await using var read = t.NewContext();
        Assert.Equal(2, await read.PracticeAnswers.CountAsync(a => a.SessionId == created.Id));
        // Câu 1 (đã nộp) giữ nguyên answer cũ (không bị thay/đè bởi việc làm câu 2).
        Assert.True(await read.PracticeAnswers.AnyAsync(a => a.Id == a1 && a.QuestionId == q1));
    }

    // ── (5) chấm dần: answer đã Scored kèm điểm SỐNG qua resume (Include Scores) ─
    [Fact]
    public async Task Resume_GiuAnswerScored_KemDiem()
    {
        using var t = new TestDb();
        var candidate = Guid.NewGuid();
        var campaignId = Guid.NewGuid();

        // INT-4 chấm dần: 1 câu đã Scored TRONG KHI buổi còn InProgress → resume phải trả answer Scored
        // kèm điểm, không mất. Seed trực tiếp (không cần đi hết đường upload→callback cho test này).
        var session = TestDb.Session(candidate, SessionStatus.InProgress, campaignId: campaignId);
        var q1 = TestDb.Question(session.Id, 1);
        var q2 = TestDb.Question(session.Id, 2);
        var crit = TestDb.Criterion(session.JobCategory, campaignId: campaignId);
        var answer = TestDb.Answer(session.Id, q1.Id, AnswerStatus.Scored, DateTime.UtcNow, DateTime.UtcNow);
        var score = new AnswerScore
        {
            Id = Guid.NewGuid(),
            AnswerId = answer.Id,
            CriterionId = crit.Id,
            Score = 4m,
            Reasoning = "tốt",
            AttemptNo = 1,
            RubricVersion = 1,
            CreatedAt = DateTime.UtcNow
        };
        await using (var seed = t.NewContext())
        {
            seed.AddRange(session, q1, q2, crit, answer, score);
            await seed.SaveChangesAsync();
        }

        // Resume: session InProgress (chưa terminal) → create-or-get trả CÙNG session (không tạo mới).
        var req = new CreateCampaignSessionRequest(
            campaignId, JobCategory.BE,
            new[] { "Q1", "Q2" },
            new[] { new CampaignCriterionInput("Clarity", null, 1.0m, 5) });
        var resumed = await BuildPractice(t.NewContext()).GetOrCreateCampaignSessionAsync(candidate, req);

        Assert.Equal(session.Id, resumed.Id);

        var a1 = resumed.Questions.Single(x => x.Id == q1.Id).Answer;
        Assert.NotNull(a1);
        Assert.Equal(nameof(AnswerStatus.Scored), a1!.Status);
        var sc = Assert.Single(a1.Scores);
        Assert.Equal(4m, sc.Score);

        // Câu 2 chưa nộp → vẫn trống (làm được sau).
        Assert.Null(resumed.Questions.Single(x => x.Id == q2.Id).Answer);
    }

    // ── (6) GetSession chủ khác → 403 (INT-11 chỉ chủ session) ────────────────
    [Fact]
    public async Task GetSession_OwnerKhac_Tra403()
    {
        using var t = new TestDb();
        var owner = Guid.NewGuid();
        var other = Guid.NewGuid();
        var session = TestDb.Session(owner, SessionStatus.InProgress, campaignId: Guid.NewGuid());
        await using (var seed = t.NewContext()) { seed.Add(session); await seed.SaveChangesAsync(); }

        var controller = BuildController(t.NewContext(), other);
        var result = await controller.GetSession(session.Id, default);

        var obj = Assert.IsType<ObjectResult>(result);
        Assert.Equal(StatusCodes.Status403Forbidden, obj.StatusCode);
    }

    // ── (7) GetSession không tồn tại → 404 ────────────────────────────────────
    [Fact]
    public async Task GetSession_KhongTonTai_Tra404()
    {
        using var t = new TestDb();
        var candidate = Guid.NewGuid();

        var controller = BuildController(t.NewContext(), candidate);
        var result = await controller.GetSession(Guid.NewGuid(), default);

        Assert.IsType<NotFoundObjectResult>(result);
    }
}
