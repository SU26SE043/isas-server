using Isas.InterviewService.ApplicationDbContext;
using Isas.InterviewService.Controllers;
using Isas.InterviewService.DTOs;
using Isas.InterviewService.Entities;
using Isas.InterviewService.Enums;
using Isas.InterviewService.Services;
using Isas.InterviewService.Services.Interfaces;
using Microsoft.AspNetCore.Mvc;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Logging.Abstractions;
using Moq;

namespace Isas.InterviewService.Tests;

/// <summary>
/// AI4 — surface transcript + nhận xét AI per-criterion + cờ needs_review cho HR (Campaign).
/// Producer phía Interview: <c>GetSessionAnswersInternalAsync</c> (máy-máy) trả per-question list kèm
/// answer đầy đủ NHƯNG BỎ check chủ session (khác GetSessionAsync). Endpoint internal (X-Internal-Token):
/// token sai → 401; session không tồn tại → 404.
/// </summary>
public class SessionTranscriptInternalAi4Tests
{
    private static PracticeService BuildPractice(InterviewDbContext db)
    {
        var notifier = new Mock<ISessionScoringNotifier>();
        notifier
            .Setup(n => n.NotifySessionScoredAsync(It.IsAny<Guid>(), It.IsAny<CancellationToken>()))
            .Returns(Task.CompletedTask);

        var reservation = new Mock<ICreditReservationClient>();
        reservation
            .Setup(r => r.ReserveAsync(It.IsAny<string>(), It.IsAny<Guid>(), It.IsAny<Guid>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(new CreditReservationResult(Guid.NewGuid(), 1));

        return new PracticeService(
            db, new Mock<IStorageService>().Object,
            new Mock<IAiServiceQuestionGenerator>().Object,
            notifier.Object, reservation.Object,
            NullLogger<PracticeService>.Instance);
    }

    // Seed 1 buổi B2B đã Scored: câu 1 có answer (transcript + điểm/nhận xét + needs_review), câu 2 chưa nộp.
    private static (Guid sessionId, Guid q1Id, Guid q2Id) SeedScoredCampaignSession(TestDb t, Guid candidate)
    {
        var campaignId = Guid.NewGuid();
        var session = TestDb.Session(candidate, SessionStatus.Scored, campaignId: campaignId);
        var q1 = TestDb.Question(session.Id, 1);
        var q2 = TestDb.Question(session.Id, 2);
        var crit = TestDb.Criterion(session.JobCategory, campaignId: campaignId, name: "Technical depth");

        var answer = TestDb.Answer(session.Id, q1.Id, AnswerStatus.Scored, DateTime.UtcNow, DateTime.UtcNow);
        answer.Transcript = "Ứng viên giải thích DI qua constructor injection và IoC container.";
        answer.NeedsReview = true;   // E10 — spread điểm vượt ngưỡng → HR soi lại

        var score = new AnswerScore
        {
            Id = Guid.NewGuid(),
            AnswerId = answer.Id,
            CriterionId = crit.Id,
            Score = 4m,
            Reasoning = "Trích transcript: 'constructor injection và IoC container' → hiểu đúng bản chất.",
            AttemptNo = 1,
            RubricVersion = 1,
            CreatedAt = DateTime.UtcNow
        };

        using var seed = t.NewContext();
        seed.AddRange(session, q1, q2, crit, answer, score);
        seed.SaveChanges();
        return (session.Id, q1.Id, q2.Id);
    }

    [Fact]
    public async Task GetSessionAnswersInternal_ReturnsTranscriptReasoningNeedsReview_NoOwnerCheck()
    {
        using var t = new TestDb();
        var candidate = Guid.NewGuid();
        var (sessionId, q1Id, q2Id) = SeedScoredCampaignSession(t, candidate);

        // Gọi KHÔNG kèm candidateId (khác GetSessionAsync) → chứng minh không enforce chủ session.
        var questions = await BuildPractice(t.NewContext()).GetSessionAnswersInternalAsync(sessionId);

        Assert.NotNull(questions);
        Assert.Equal(2, questions!.Count);

        // Câu 1: có answer với transcript + điểm + nhận xét + needs_review.
        var a1 = questions.Single(q => q.Id == q1Id).Answer;
        Assert.NotNull(a1);
        Assert.Equal(nameof(AnswerStatus.Scored), a1!.Status);
        Assert.Contains("IoC container", a1.Transcript);
        Assert.True(a1.NeedsReview);
        var sc = Assert.Single(a1.Scores);
        Assert.Equal(4m, sc.Score);
        Assert.Contains("constructor injection", sc.Reasoning);

        // Câu 2: chưa nộp → answer trống (vẫn liệt kê câu hỏi).
        Assert.Null(questions.Single(q => q.Id == q2Id).Answer);
    }

    [Fact]
    public async Task GetSessionAnswersInternal_MissingSession_ReturnsNull()
    {
        using var t = new TestDb();
        var result = await BuildPractice(t.NewContext()).GetSessionAnswersInternalAsync(Guid.NewGuid());
        Assert.Null(result);
    }

    private static InternalSessionsController BuildController(TestDb t)
    {
        var config = new ConfigurationBuilder().AddInMemoryCollection(new Dictionary<string, string?>
        {
            ["Internal:Token"] = "test-internal-token"
        }).Build();
        return new InternalSessionsController(
            BuildPractice(t.NewContext()), config, NullLogger<InternalSessionsController>.Instance);
    }

    [Fact]
    public async Task InternalEndpoint_BadToken_Returns401()
    {
        using var t = new TestDb();
        var candidate = Guid.NewGuid();
        var (sessionId, _, _) = SeedScoredCampaignSession(t, candidate);

        var result = await BuildController(t).GetSessionAnswers(sessionId, token: "wrong-token", default);
        Assert.IsType<UnauthorizedObjectResult>(result);
    }

    [Fact]
    public async Task InternalEndpoint_MissingSession_Returns404()
    {
        using var t = new TestDb();
        var result = await BuildController(t).GetSessionAnswers(Guid.NewGuid(), token: "test-internal-token", default);
        Assert.IsType<NotFoundObjectResult>(result);
    }

    [Fact]
    public async Task InternalEndpoint_ValidToken_ReturnsAnswers()
    {
        using var t = new TestDb();
        var candidate = Guid.NewGuid();
        var (sessionId, _, _) = SeedScoredCampaignSession(t, candidate);

        var result = await BuildController(t).GetSessionAnswers(sessionId, token: "test-internal-token", default);
        var ok = Assert.IsType<OkObjectResult>(result);
        var questions = Assert.IsAssignableFrom<IReadOnlyList<Isas.InterviewService.DTOs.QuestionResponse>>(ok.Value);
        Assert.Equal(2, questions.Count);
    }
}
