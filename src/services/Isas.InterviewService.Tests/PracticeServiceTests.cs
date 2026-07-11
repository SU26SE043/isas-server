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
        => Build(t, gen, out _);

    private static PracticeService Build(
        TestDb t, Mock<IAiServiceQuestionGenerator> gen, out Mock<ISessionScoringNotifier> scoringNotifier)
    {
        scoringNotifier = new Mock<ISessionScoringNotifier>();
        scoringNotifier
            .Setup(n => n.NotifySessionScoredAsync(It.IsAny<Guid>(), It.IsAny<CancellationToken>()))
            .Returns(Task.CompletedTask);

        return new PracticeService(
            t.Db, new Mock<IStorageService>().Object, gen.Object, scoringNotifier.Object,
            NullLogger<PracticeService>.Instance);
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

    [Fact]
    public async Task Create_GeneratorReturnsEmpty_SessionFailed_Throws()
    {
        using var t = new TestDb();
        var candidate = Guid.NewGuid();

        var gen = new Mock<IAiServiceQuestionGenerator>();
        gen.Setup(g => g.GenerateQuestionsAsync(
                It.IsAny<string>(), It.IsAny<string?>(), It.IsAny<string?>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(new List<GeneratedQuestion>());

        var svc = Build(t, gen);
        var req = new CreatePracticeSessionRequest(null, null, JobCategory.FE);

        await Assert.ThrowsAsync<InvalidOperationException>(() =>
            svc.CreateSessionAsync(candidate, req));

        // Session phải được đánh dấu Failed (không để treo GeneratingQuestions).
        var s = await t.Db.PracticeSessions.AsNoTracking().FirstAsync(x => x.CandidateId == candidate);
        Assert.Equal(SessionStatus.Failed, s.Status);
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
}
