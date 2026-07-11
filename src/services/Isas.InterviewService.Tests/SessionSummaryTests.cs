using Isas.InterviewService.DTOs;
using Isas.InterviewService.Entities;
using Isas.InterviewService.Enums;
using Isas.InterviewService.Services;
using Isas.InterviewService.Services.Interfaces;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging.Abstractions;
using Moq;

namespace Isas.InterviewService.Tests;

// BC10 — nhận xét chung buổi luyện B2C (AI best-effort). Nửa Interview: wiring SessionScoringNotifier
// gọi AIService /summarize-session (mock) khi Scored → lưu practice_sessions.overall_comment; GET trả về.
// AI lỗi KHÔNG chặn Scored (best-effort); B2B không sinh.
public class SessionSummaryTests
{
    private static AnswerScore Score(Guid answerId, Guid criterionId, decimal score)
        => new()
        {
            Id = Guid.NewGuid(),
            AnswerId = answerId,
            CriterionId = criterionId,
            AttemptNo = 1,
            Score = score,
            Reasoning = "x",
            RubricVersion = 1,
            CreatedAt = DateTime.UtcNow
        };

    private static RubricCriterion Crit(JobCategory cat, string name, int maxScore, decimal weight, Guid? campaignId = null)
        => new()
        {
            Id = Guid.NewGuid(),
            Name = name,
            Description = name,
            Weight = weight,
            MaxScore = maxScore,
            IsActive = true,
            JobCategory = cat,
            CampaignId = campaignId,
            Version = 1
        };

    // Notifier THẬT + result service THẬT (BC9) — chỉ mock transport event + summarizer AI (BC10).
    private static SessionScoringNotifier BuildNotifier(TestDb t, IAiServiceSessionSummarizer summarizer)
    {
        var eventPub = new Mock<ISessionEventPublisher>();
        eventPub
            .Setup(p => p.PublishSessionScoredAsync(It.IsAny<SessionScoredEvent>(), It.IsAny<CancellationToken>()))
            .Returns(Task.CompletedTask);
        return new SessionScoringNotifier(
            t.Db, eventPub.Object, TestDb.ResultService(t.Db), summarizer,
            TestDb.RoadmapReport(t.Db), NullLogger<SessionScoringNotifier>.Instance);
    }

    private static PracticeService BuildPractice(TestDb t)
    {
        var notifier = new Mock<ISessionScoringNotifier>();
        notifier
            .Setup(n => n.NotifySessionScoredAsync(It.IsAny<Guid>(), It.IsAny<CancellationToken>()))
            .Returns(Task.CompletedTask);
        return new PracticeService(
            t.Db, new Mock<IStorageService>().Object,
            new Mock<IAiServiceQuestionGenerator>().Object, notifier.Object,
            new Mock<ICreditReservationClient>().Object,
            new Mock<ISessionEventPublisher>().Object,
            NullLogger<PracticeService>.Instance);
    }

    // Seed 1 buổi B2C có 1 tiêu chí đã chấm (đủ để BC9 ghi breakdown → BC10 có số liệu để nhận xét).
    private static (PracticeSession session, Guid candidate) SeedScoredB2C(TestDb t)
    {
        var candidate = Guid.NewGuid();
        var session = TestDb.Session(candidate, SessionStatus.Scored, JobCategory.BE);
        var q = TestDb.Question(session.Id);
        var crit = Crit(JobCategory.BE, "Clarity", maxScore: 5, weight: 1.0m);
        var answer = TestDb.Answer(session.Id, q.Id, AnswerStatus.Scored, DateTime.UtcNow, DateTime.UtcNow);
        t.Db.AddRange(session, q, crit, answer);
        t.Db.Add(Score(answer.Id, crit.Id, 4m));   // 4/5 = 80%
        t.Db.SaveChanges();
        return (session, candidate);
    }

    // (1) B2C Scored → summarizer trả text → overall_comment được lưu.
    [Fact]
    public async Task Summarize_B2CSession_WhenScored_PersistsOverallComment()
    {
        using var t = new TestDb();
        var (session, _) = SeedScoredB2C(t);
        const string comment = "Bạn trình bày rõ ràng nhưng cần đào sâu ví dụ thực tế.";

        var notifier = BuildNotifier(t, TestDb.Summarizer(comment));
        await notifier.NotifySessionScoredAsync(session.Id);

        var s = await t.NewContext().PracticeSessions.AsNoTracking().FirstAsync(x => x.Id == session.Id);
        Assert.Equal(comment, s.OverallComment);
        Assert.Equal(80m, s.OverallScore);   // BC9 vẫn ghi số liệu (không bị BC10 phá)
    }

    // (2) AI ném lỗi → session vẫn Scored + overall_comment null (best-effort KHÔNG chặn Scored).
    [Fact]
    public async Task Summarize_AiThrows_SessionStillScored_CommentNull()
    {
        using var t = new TestDb();
        var (session, _) = SeedScoredB2C(t);

        var notifier = BuildNotifier(t, TestDb.Summarizer(throws: new AiServiceException("AI sập")));
        // Không được ném ra ngoài (nuốt lỗi).
        await notifier.NotifySessionScoredAsync(session.Id);

        var s = await t.NewContext().PracticeSessions.AsNoTracking().FirstAsync(x => x.Id == session.Id);
        Assert.Null(s.OverallComment);
        Assert.Equal(SessionStatus.Scored, s.Status);
        Assert.Equal(80m, s.OverallScore);   // BC9 số liệu vẫn còn (Scored đầy đủ)
    }

    // (3) B2B (campaign_id != null) → KHÔNG gọi summarizer (no-op BC10).
    [Fact]
    public async Task Summarize_B2BSession_DoesNotCallSummarizer()
    {
        using var t = new TestDb();
        var candidate = Guid.NewGuid();
        var campaignId = Guid.NewGuid();
        var session = TestDb.Session(candidate, SessionStatus.Scored, JobCategory.BE, campaignId: campaignId);
        var q = TestDb.Question(session.Id);
        var crit = Crit(JobCategory.BE, "Clarity", maxScore: 5, weight: 1.0m, campaignId: campaignId);
        var answer = TestDb.Answer(session.Id, q.Id, AnswerStatus.Scored, DateTime.UtcNow, DateTime.UtcNow);
        t.Db.AddRange(session, q, crit, answer);
        t.Db.Add(Score(answer.Id, crit.Id, 4m));
        await t.Db.SaveChangesAsync();

        var summarizer = new Mock<IAiServiceSessionSummarizer>();
        var notifier = BuildNotifier(t, summarizer.Object);
        await notifier.NotifySessionScoredAsync(session.Id);

        summarizer.Verify(s => s.SummarizeAsync(
            It.IsAny<string>(), It.IsAny<decimal>(),
            It.IsAny<IReadOnlyList<SessionSummaryCriterion>>(), It.IsAny<CancellationToken>()),
            Times.Never);

        var s = await t.NewContext().PracticeSessions.AsNoTracking().FirstAsync(x => x.Id == session.Id);
        Assert.Null(s.OverallComment);   // B2B không sinh nhận xét
    }

    // (4) GET /sessions/{id} → result.overallComment đọc từ DB.
    [Fact]
    public async Task GetSession_ReturnsOverallComment()
    {
        using var t = new TestDb();
        var (session, candidate) = SeedScoredB2C(t);
        const string comment = "Nhận xét chung: khá tốt, cần luyện thêm phần thiết kế.";

        // BC9 số liệu + BC10 nhận xét đã lưu.
        await TestDb.ResultService(t.Db).ComputeAndStoreAsync(session.Id);
        await t.Db.PracticeSessions.Where(x => x.Id == session.Id)
            .ExecuteUpdateAsync(u => u.SetProperty(x => x.OverallComment, comment));

        var resp = await BuildPractice(t).GetSessionAsync(candidate, session.Id);
        Assert.NotNull(resp!.Result);
        Assert.Equal(comment, resp.Result!.OverallComment);
    }
}
