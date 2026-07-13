using Isas.InterviewService.DTOs;
using Isas.InterviewService.Enums;
using Isas.InterviewService.Services;
using Isas.InterviewService.Services.Interfaces;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging.Abstractions;
using Moq;

namespace Isas.InterviewService.Tests;

public class PracticeServiceTests
{
    private static PracticeService Build(TestDb t, Mock<IAiServiceQuestionGenerator> gen)
        => Build(t, gen, out _, out _, out _);

    private static PracticeService Build(
        TestDb t, Mock<IAiServiceQuestionGenerator> gen, out Mock<ISessionScoringNotifier> scoringNotifier)
        => Build(t, gen, out scoringNotifier, out _, out _);

    private static PracticeService Build(
        TestDb t, Mock<IAiServiceQuestionGenerator> gen,
        out Mock<ISessionScoringNotifier> scoringNotifier,
        out Mock<ICreditReservationClient> reservation)
        => Build(t, gen, out scoringNotifier, out reservation, out _);

    // BC2: mặc định reserve (owner=User) THÀNH CÔNG → luồng tạo session chạy như cũ.
    // Test 402/verify lấy `reservation` ra để setup/verify riêng.
    // BK12: `eventPublisher` để verify phát SessionAbandoned(generation_failed) khi session Failed.
    private static PracticeService Build(
        TestDb t, Mock<IAiServiceQuestionGenerator> gen,
        out Mock<ISessionScoringNotifier> scoringNotifier,
        out Mock<ICreditReservationClient> reservation,
        out Mock<ISessionEventPublisher> eventPublisher)
    {
        scoringNotifier = new Mock<ISessionScoringNotifier>();
        scoringNotifier
            .Setup(n => n.NotifySessionScoredAsync(It.IsAny<Guid>(), It.IsAny<CancellationToken>()))
            .Returns(Task.CompletedTask);

        reservation = new Mock<ICreditReservationClient>();
        reservation
            .Setup(r => r.ReserveAsync("User", It.IsAny<Guid>(), It.IsAny<Guid>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(new CreditReservationResult(Guid.NewGuid(), 1));

        eventPublisher = new Mock<ISessionEventPublisher>();

        return new PracticeService(
            t.Db, new Mock<IStorageService>().Object, gen.Object, scoringNotifier.Object,
            reservation.Object, eventPublisher.Object, NullLogger<PracticeService>.Instance);
    }

    [Fact]
    public async Task Create_HappyPath_SessionReady_WithQuestions()
    {
        using var t = new TestDb();
        var candidate = Guid.NewGuid();

        var gen = new Mock<IAiServiceQuestionGenerator>();
        gen.Setup(g => g.GenerateQuestionsAsync(
                It.IsAny<string>(), It.IsAny<string?>(), It.IsAny<string?>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(new List<GeneratedQuestion>
            {
                new() { Content = "Q1" }, new() { Content = "Q2" }, new() { Content = "Q3" }
            });

        var svc = Build(t, gen);
        var req = new CreatePracticeSessionRequest(null, null, JobCategory.BE);

        var res = await svc.CreateSessionAsync(candidate, req);

        Assert.Equal(nameof(SessionStatus.Ready), res.Status);
        Assert.Equal(3, res.Questions.Count);
        Assert.Equal(1, res.Questions[0].OrderNo);

        var saved = await t.Db.PracticeQuestions.AsNoTracking()
            .CountAsync(q => q.SessionId == res.Id);
        Assert.Equal(3, saved);
    }

    // BC2 (a): reserve OK → tạo session + reserve đúng ví cá nhân (owner=User, ownerId=candidate,
    // sessionId = Id session vừa tạo). Idempotency khớp session thật.
    [Fact]
    public async Task Create_ReserveOk_CreatesSession_AndReservesPersonalWallet()
    {
        using var t = new TestDb();
        var candidate = Guid.NewGuid();

        var gen = new Mock<IAiServiceQuestionGenerator>();
        gen.Setup(g => g.GenerateQuestionsAsync(
                It.IsAny<string>(), It.IsAny<string?>(), It.IsAny<string?>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(new List<GeneratedQuestion> { new() { Content = "Q1" } });

        var svc = Build(t, gen, out _, out var reservation);
        var req = new CreatePracticeSessionRequest(null, null, JobCategory.BE);

        var res = await svc.CreateSessionAsync(candidate, req);

        Assert.Equal(nameof(SessionStatus.Ready), res.Status);
        // reserve gọi đúng: owner=User, ownerId=candidate, sessionId = Id session vừa tạo.
        reservation.Verify(r => r.ReserveAsync("User", candidate, res.Id, It.IsAny<CancellationToken>()),
            Times.Once);

        var count = await t.Db.PracticeSessions.AsNoTracking().CountAsync(s => s.CandidateId == candidate);
        Assert.Equal(1, count);
    }

    // BC2 (b): ví hết credit → Payment 402 → InsufficientCreditException; KHÔNG có row session,
    // và KHÔNG gọi AI sinh câu hỏi (reserve chặn trước).
    [Fact]
    public async Task Create_ReserveReturns402_NoSessionRow_AndSkipsQuestionGen()
    {
        using var t = new TestDb();
        var candidate = Guid.NewGuid();

        var gen = new Mock<IAiServiceQuestionGenerator>();

        var svc = Build(t, gen, out _, out var reservation);
        reservation
            .Setup(r => r.ReserveAsync("User", It.IsAny<Guid>(), It.IsAny<Guid>(), It.IsAny<CancellationToken>()))
            .ThrowsAsync(new InsufficientCreditException("Ví không đủ credit"));

        var req = new CreatePracticeSessionRequest(null, null, JobCategory.FE);

        await Assert.ThrowsAsync<InsufficientCreditException>(() =>
            svc.CreateSessionAsync(candidate, req));

        // Không có row session (PAY-5) — cũng không có câu hỏi.
        Assert.Equal(0, await t.Db.PracticeSessions.CountAsync());
        Assert.Equal(0, await t.Db.PracticeQuestions.CountAsync());
        // Reserve chặn trước AI → không tốn 1 lượt gọi Gemini.
        gen.Verify(g => g.GenerateQuestionsAsync(
                It.IsAny<string>(), It.IsAny<string?>(), It.IsAny<string?>(), It.IsAny<CancellationToken>()),
            Times.Never);
    }

    // BC2 (c): tạo session B2B (campaign) KHÔNG reserve ví cá nhân (B2B reserve do Campaign, PAY-6).
    [Fact]
    public async Task CreateCampaignSession_DoesNotReservePersonalWallet()
    {
        using var t = new TestDb();
        var candidate = Guid.NewGuid();

        var svc = Build(t, new Mock<IAiServiceQuestionGenerator>(), out _, out var reservation);

        var req = new CreateCampaignSessionRequest(
            Guid.NewGuid(), JobCategory.BE,
            Questions: new[] { "Q1" },
            Criteria: new[] { new CampaignCriterionInput("Technical depth", null, 1.0m, 5) });

        await svc.CreateCampaignSessionAsync(candidate, req);

        reservation.Verify(r => r.ReserveAsync(
                It.IsAny<string>(), It.IsAny<Guid>(), It.IsAny<Guid>(), It.IsAny<CancellationToken>()),
            Times.Never);
    }

    [Fact]
    public async Task Create_GeneratorReturnsEmpty_SessionFailed_Throws()
    {
        using var t = new TestDb();
        var candidate = Guid.NewGuid();

        var gen = new Mock<IAiServiceQuestionGenerator>();
        gen.Setup(g => g.GenerateQuestionsAsync(
                It.IsAny<string>(), It.IsAny<string?>(), It.IsAny<string?>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(new List<GeneratedQuestion>());

        var svc = Build(t, gen, out _, out _, out var publisher);
        var req = new CreatePracticeSessionRequest(null, null, JobCategory.FE);

        await Assert.ThrowsAsync<InvalidOperationException>(() =>
            svc.CreateSessionAsync(candidate, req));

        // Session phải được đánh dấu Failed (không để treo GeneratingQuestions).
        var s = await t.Db.PracticeSessions.AsNoTracking().FirstAsync(x => x.CandidateId == candidate);
        Assert.Equal(SessionStatus.Failed, s.Status);

        // BK12: AIService trả rỗng cũng là "sinh câu hỏi lỗi" sau reserve → phát abandoned để release credit.
        publisher.Verify(p => p.PublishSessionAbandonedAsync(
            It.Is<SessionAbandonedEvent>(e => e.SessionId == s.Id && e.Reason == "generation_failed"),
            It.IsAny<CancellationToken>()), Times.Once);
    }

    // BK12 (a): B2C reserve → AI sinh câu hỏi NÉM lỗi → Failed → phát SessionAbandoned đúng sessionId
    // + reason=generation_failed (E7 nghe để release credit ví User; nếu không có → orphan credit BC2).
    [Fact]
    public async Task Create_GeneratorThrows_SessionFailed_PublishesAbandonedToReleaseCredit()
    {
        using var t = new TestDb();
        var candidate = Guid.NewGuid();

        var gen = new Mock<IAiServiceQuestionGenerator>();
        gen.Setup(g => g.GenerateQuestionsAsync(
                It.IsAny<string>(), It.IsAny<string?>(), It.IsAny<string?>(), It.IsAny<CancellationToken>()))
            .ThrowsAsync(new Exception("Gemini down"));

        var svc = Build(t, gen, out _, out _, out var publisher);
        var req = new CreatePracticeSessionRequest(null, null, JobCategory.BE);

        await Assert.ThrowsAsync<InvalidOperationException>(() =>
            svc.CreateSessionAsync(candidate, req));

        var s = await t.Db.PracticeSessions.AsNoTracking().FirstAsync(x => x.CandidateId == candidate);
        Assert.Equal(SessionStatus.Failed, s.Status);

        // B2C: campaign_id null; event mang đúng session/candidate + reason chuẩn.
        publisher.Verify(p => p.PublishSessionAbandonedAsync(
            It.Is<SessionAbandonedEvent>(e =>
                e.SessionId == s.Id && e.CandidateId == candidate
                && e.CampaignId == null && e.Reason == "generation_failed"),
            It.IsAny<CancellationToken>()), Times.Once);
    }

    // BK12 (b): phát event là BEST-EFFORT — publisher ném lỗi KHÔNG được chặn luồng: session vẫn
    // Failed trong DB và vẫn ném InvalidOperationException gốc (không nuốt mất lỗi sinh câu hỏi).
    [Fact]
    public async Task Create_GeneratorFails_PublishThrows_StillFailedAndThrows()
    {
        using var t = new TestDb();
        var candidate = Guid.NewGuid();

        var gen = new Mock<IAiServiceQuestionGenerator>();
        gen.Setup(g => g.GenerateQuestionsAsync(
                It.IsAny<string>(), It.IsAny<string?>(), It.IsAny<string?>(), It.IsAny<CancellationToken>()))
            .ThrowsAsync(new Exception("Gemini down"));

        var svc = Build(t, gen, out _, out _, out var publisher);
        publisher
            .Setup(p => p.PublishSessionAbandonedAsync(
                It.IsAny<SessionAbandonedEvent>(), It.IsAny<CancellationToken>()))
            .ThrowsAsync(new Exception("broker down"));

        var req = new CreatePracticeSessionRequest(null, null, JobCategory.FE);

        await Assert.ThrowsAsync<InvalidOperationException>(() =>
            svc.CreateSessionAsync(candidate, req));

        var s = await t.Db.PracticeSessions.AsNoTracking().FirstAsync(x => x.CandidateId == candidate);
        Assert.Equal(SessionStatus.Failed, s.Status);
    }

    // BK12 (c): đường THÀNH CÔNG (session Ready, không Failed) KHÔNG phát SessionAbandoned.
    [Fact]
    public async Task Create_HappyPath_DoesNotPublishAbandoned()
    {
        using var t = new TestDb();
        var candidate = Guid.NewGuid();

        var gen = new Mock<IAiServiceQuestionGenerator>();
        gen.Setup(g => g.GenerateQuestionsAsync(
                It.IsAny<string>(), It.IsAny<string?>(), It.IsAny<string?>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(new List<GeneratedQuestion> { new() { Content = "Q1" } });

        var svc = Build(t, gen, out _, out _, out var publisher);
        var req = new CreatePracticeSessionRequest(null, null, JobCategory.BE);

        var res = await svc.CreateSessionAsync(candidate, req);

        Assert.Equal(nameof(SessionStatus.Ready), res.Status);
        publisher.Verify(p => p.PublishSessionAbandonedAsync(
            It.IsAny<SessionAbandonedEvent>(), It.IsAny<CancellationToken>()), Times.Never);
    }

    [Fact]
    public async Task Submit_NoAnswers_Throws()
    {
        using var t = new TestDb();
        var candidate = Guid.NewGuid();
        var session = TestDb.Session(candidate, SessionStatus.InProgress);
        t.Db.Add(session);
        await t.Db.SaveChangesAsync();

        var svc = Build(t, new Mock<IAiServiceQuestionGenerator>());

        await Assert.ThrowsAsync<InvalidOperationException>(() =>
            svc.SubmitSessionAsync(candidate, session.Id));
    }

    [Fact]
    public async Task Submit_WrongStatus_Throws()
    {
        using var t = new TestDb();
        var candidate = Guid.NewGuid();
        var session = TestDb.Session(candidate, SessionStatus.Scored);
        var q = TestDb.Question(session.Id);
        var a = TestDb.Answer(session.Id, q.Id, AnswerStatus.Scored, DateTime.UtcNow, DateTime.UtcNow);
        t.Db.AddRange(session, q, a);
        await t.Db.SaveChangesAsync();

        var svc = Build(t, new Mock<IAiServiceQuestionGenerator>());

        await Assert.ThrowsAsync<InvalidOperationException>(() =>
            svc.SubmitSessionAsync(candidate, session.Id));
    }

    [Fact]
    public async Task Submit_WrongCandidate_Throws()
    {
        using var t = new TestDb();
        var owner = Guid.NewGuid();
        var session = TestDb.Session(owner, SessionStatus.InProgress);
        t.Db.Add(session);
        await t.Db.SaveChangesAsync();

        var svc = Build(t, new Mock<IAiServiceQuestionGenerator>());

        await Assert.ThrowsAsync<UnauthorizedAccessException>(() =>
            svc.SubmitSessionAsync(Guid.NewGuid(), session.Id));
    }

    [Fact]
    public async Task Submit_AllAnswersAlreadyScored_ClosesSessionToScored()
    {
        using var t = new TestDb();
        var candidate = Guid.NewGuid();
        var session = TestDb.Session(candidate, SessionStatus.InProgress);
        var q = TestDb.Question(session.Id);
        // Chấm dần: answer đã Scored TRƯỚC khi submit -> submit phải đóng luôn sang Scored.
        var a = TestDb.Answer(session.Id, q.Id, AnswerStatus.Scored, DateTime.UtcNow, DateTime.UtcNow);
        t.Db.AddRange(session, q, a);
        await t.Db.SaveChangesAsync();

        var svc = Build(t, new Mock<IAiServiceQuestionGenerator>(), out var notifier);
        await svc.SubmitSessionAsync(candidate, session.Id);

        var s = await t.Db.PracticeSessions.AsNoTracking().FirstAsync(x => x.Id == session.Id);
        Assert.Equal(SessionStatus.Scored, s.Status);

        // E2: nhánh "đóng-ngay" của submit (mọi answer đã Scored từ trước, chấm dần xong sớm)
        // CŨNG phải phát SessionScored — không chỉ nhánh đóng qua callback ở AnswerService.
        notifier.Verify(n => n.NotifySessionScoredAsync(session.Id, It.IsAny<CancellationToken>()),
            Times.Once);
    }

    // I2 (D21): chốt buổi theo từng câu — câu CHƯA trả lời → answer `Skipped`; câu đã trả lời giữ nguyên.
    // Mọi answer done (Scored + Skipped) → session Scored (không kẹt Scoring vì câu trống).
    [Fact]
    public async Task Submit_UnansweredQuestions_MarkedSkipped_AndClosesToScored()
    {
        using var t = new TestDb();
        var candidate = Guid.NewGuid();
        var session = TestDb.Session(candidate, SessionStatus.InProgress);
        var q1 = TestDb.Question(session.Id, 1);
        var q2 = TestDb.Question(session.Id, 2);
        // q1 trả lời + đã Scored; q2 chưa trả lời.
        var a1 = TestDb.Answer(session.Id, q1.Id, AnswerStatus.Scored, DateTime.UtcNow, DateTime.UtcNow);
        t.Db.AddRange(session, q1, q2, a1);
        await t.Db.SaveChangesAsync();

        var svc = Build(t, new Mock<IAiServiceQuestionGenerator>(), out var notifier);
        await svc.SubmitSessionAsync(candidate, session.Id);

        var s = await t.Db.PracticeSessions.AsNoTracking().FirstAsync(x => x.Id == session.Id);
        Assert.Equal(SessionStatus.Scored, s.Status);

        var answers = await t.Db.PracticeAnswers.AsNoTracking()
            .Where(x => x.SessionId == session.Id).ToListAsync();
        Assert.Equal(2, answers.Count);
        Assert.Contains(answers, x => x.QuestionId == q2.Id && x.Status == AnswerStatus.Skipped);

        notifier.Verify(n => n.NotifySessionScoredAsync(session.Id, It.IsAny<CancellationToken>()), Times.Once);
    }

    // I2: câu chưa trả lời → Skipped, NHƯNG answer đang chấm (Uploaded) chưa xong → buổi giữ Scoring
    // (chờ chấm nốt) — Skipped không "ép" đóng buổi khi còn answer thật chưa xong.
    [Fact]
    public async Task Submit_UnansweredQuestion_WithPendingAnswer_MarksSkipped_StaysScoring()
    {
        using var t = new TestDb();
        var candidate = Guid.NewGuid();
        var session = TestDb.Session(candidate, SessionStatus.InProgress);
        var q1 = TestDb.Question(session.Id, 1);
        var q2 = TestDb.Question(session.Id, 2);
        // q1 đang chờ chấm (Uploaded), q2 chưa trả lời.
        var a1 = TestDb.Answer(session.Id, q1.Id, AnswerStatus.Uploaded, DateTime.UtcNow, DateTime.UtcNow);
        t.Db.AddRange(session, q1, q2, a1);
        await t.Db.SaveChangesAsync();

        var svc = Build(t, new Mock<IAiServiceQuestionGenerator>(), out var notifier);
        await svc.SubmitSessionAsync(candidate, session.Id);

        var s = await t.Db.PracticeSessions.AsNoTracking().FirstAsync(x => x.Id == session.Id);
        Assert.Equal(SessionStatus.Scoring, s.Status);   // còn a1 Uploaded → chưa đóng

        var a2 = await t.Db.PracticeAnswers.AsNoTracking()
            .FirstAsync(x => x.SessionId == session.Id && x.QuestionId == q2.Id);
        Assert.Equal(AnswerStatus.Skipped, a2.Status);

        notifier.Verify(n => n.NotifySessionScoredAsync(It.IsAny<Guid>(), It.IsAny<CancellationToken>()), Times.Never);
    }

    // I2: CreateCampaignSession nhận ExpiresAt (hạn nhận bài B2B) → set session.Deadline.
    [Fact]
    public async Task CreateCampaignSession_WithExpiresAt_SetsDeadline()
    {
        using var t = new TestDb();
        var candidate = Guid.NewGuid();
        var expires = DateTime.UtcNow.AddHours(2);

        var svc = Build(t, new Mock<IAiServiceQuestionGenerator>());
        var req = new CreateCampaignSessionRequest(
            Guid.NewGuid(), JobCategory.BE,
            Questions: new[] { "Q1" },
            Criteria: new[] { new CampaignCriterionInput("Technical depth", null, 1.0m, 5) },
            ExpiresAt: expires);

        var res = await svc.CreateCampaignSessionAsync(candidate, req);

        var s = await t.Db.PracticeSessions.AsNoTracking().FirstAsync(x => x.Id == res.Id);
        Assert.NotNull(s.Deadline);
        Assert.True(Math.Abs((s.Deadline!.Value - expires).TotalSeconds) < 2);
    }

    // I2: ExpiresAt null (không truyền) → Deadline null (B2C hoặc B2B chưa cấu hình hạn nhận bài).
    [Fact]
    public async Task CreateCampaignSession_NoExpiresAt_DeadlineNull()
    {
        using var t = new TestDb();
        var candidate = Guid.NewGuid();

        var svc = Build(t, new Mock<IAiServiceQuestionGenerator>());
        var req = new CreateCampaignSessionRequest(
            Guid.NewGuid(), JobCategory.BE,
            Questions: new[] { "Q1" },
            Criteria: new[] { new CampaignCriterionInput("Technical depth", null, 1.0m, 5) });

        var res = await svc.CreateCampaignSessionAsync(candidate, req);

        var s = await t.Db.PracticeSessions.AsNoTracking().FirstAsync(x => x.Id == res.Id);
        Assert.Null(s.Deadline);
    }
}
