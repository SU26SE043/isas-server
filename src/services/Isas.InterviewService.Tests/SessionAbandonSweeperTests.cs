using System.Reflection;
using Isas.InterviewService.ApplicationDbContext;
using Isas.InterviewService.DTOs;
using Isas.InterviewService.Enums;
using Isas.InterviewService.Services;
using Isas.InterviewService.Services.Interfaces;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging.Abstractions;
using Moq;

namespace Isas.InterviewService.Tests;

public class SessionAbandonSweeperTests
{
    // Gọi ScanOnceAsync (private) một nhịp.
    private static async Task ScanOnce(SessionAbandonSweeper s)
    {
        var mi = typeof(SessionAbandonSweeper)
            .GetMethod("ScanOnceAsync", BindingFlags.NonPublic | BindingFlags.Instance)!;
        await (Task)mi.Invoke(s, new object[] { CancellationToken.None })!;
    }

    // ServiceProvider thật để CreateScope() trả về DbContext dùng chung connection (giống
    // StuckAnswerRepublisherTests.Build).
    private static (SessionAbandonSweeper sweeper, Mock<ISessionEventPublisher> pub) Build(TestDb t)
    {
        var services = new ServiceCollection();
        services.AddDbContext<InterviewDbContext>(o => o.UseSqlite(t.Connection));
        var provider = services.BuildServiceProvider();

        var pub = new Mock<ISessionEventPublisher>();
        var sweeper = new SessionAbandonSweeper(
            provider.GetRequiredService<IServiceScopeFactory>(),
            pub.Object,
            NullLogger<SessionAbandonSweeper>.Instance);
        return (sweeper, pub);
    }

    [Fact]
    public async Task InProgress_PastDeadline_ZeroAnswers_PublishesSessionAbandoned_AndClosesSession()
    {
        using var t = new TestDb();
        var candidateId = Guid.NewGuid();
        var campaignId = Guid.NewGuid();
        // I2: InProgress, Deadline (hạn nhận bài) đã qua, KHÔNG có answer nào -> SessionAbandoned.
        var session = TestDb.Session(candidateId, SessionStatus.InProgress, campaignId: campaignId,
            deadline: DateTime.UtcNow.AddMinutes(-5));
        t.Db.Add(session);
        await t.Db.SaveChangesAsync();

        var (sweeper, pub) = Build(t);
        SessionAbandonedEvent? published = null;
        pub.Setup(p => p.PublishSessionAbandonedAsync(It.IsAny<SessionAbandonedEvent>(), It.IsAny<CancellationToken>()))
           .Callback<SessionAbandonedEvent, CancellationToken>((e, _) => published = e)
           .Returns(Task.CompletedTask);

        await ScanOnce(sweeper);

        pub.Verify(p => p.PublishSessionAbandonedAsync(
            It.IsAny<SessionAbandonedEvent>(), It.IsAny<CancellationToken>()), Times.Once);

        Assert.NotNull(published);
        Assert.Equal(session.Id, published!.SessionId);
        Assert.Equal(campaignId, published.CampaignId);
        Assert.Equal(candidateId, published.CandidateId);
        Assert.False(string.IsNullOrWhiteSpace(published.Reason));
        Assert.True(published.AbandonedAt > DateTime.UtcNow.AddMinutes(-1));

        var saved = await t.NewContext().PracticeSessions.AsNoTracking().FirstAsync(x => x.Id == session.Id);
        Assert.Equal(SessionStatus.SessionAbandoned, saved.Status);
        Assert.NotNull(saved.CompletedAt);
    }

    [Fact]
    public async Task InProgress_NullDeadline_WithAnswer_NotTouched()
    {
        // I2: B2C (Deadline null) → KHÔNG hard-deadline → sweeper bỏ qua dù có answer & tạo lâu.
        using var t = new TestDb();
        var session = TestDb.Session(Guid.NewGuid(), SessionStatus.InProgress,
            createdAt: DateTime.UtcNow.AddMinutes(-120), deadline: null);
        var q = TestDb.Question(session.Id);
        var a = TestDb.Answer(session.Id, q.Id, AnswerStatus.Uploaded,
            DateTime.UtcNow.AddMinutes(-120), lastPublished: null);
        t.Db.AddRange(session, q, a);
        await t.Db.SaveChangesAsync();

        var (sweeper, pub) = Build(t);
        await ScanOnce(sweeper);

        pub.Verify(p => p.PublishSessionAbandonedAsync(
            It.IsAny<SessionAbandonedEvent>(), It.IsAny<CancellationToken>()), Times.Never);

        var saved = await t.NewContext().PracticeSessions.AsNoTracking().FirstAsync(x => x.Id == session.Id);
        Assert.Equal(SessionStatus.InProgress, saved.Status);   // không auto-submit, không abandon
    }

    [Fact]
    public async Task InProgress_DeadlineNotYetPassed_ZeroAnswers_NotAbandoned()
    {
        using var t = new TestDb();
        // I2: Deadline còn ở tương lai → chưa quá hạn → không đụng.
        var session = TestDb.Session(Guid.NewGuid(), SessionStatus.InProgress,
            deadline: DateTime.UtcNow.AddMinutes(30));
        t.Db.Add(session);
        await t.Db.SaveChangesAsync();

        var (sweeper, pub) = Build(t);
        await ScanOnce(sweeper);

        pub.Verify(p => p.PublishSessionAbandonedAsync(
            It.IsAny<SessionAbandonedEvent>(), It.IsAny<CancellationToken>()), Times.Never);

        var saved = await t.NewContext().PracticeSessions.AsNoTracking().FirstAsync(x => x.Id == session.Id);
        Assert.Equal(SessionStatus.InProgress, saved.Status);
    }

    [Fact]
    public async Task ReadyStatus_PastDeadline_ZeroAnswers_NotAbandoned()
    {
        // Sweeper chỉ chạm InProgress — Ready (chưa bắt đầu) quá Deadline KHÔNG thuộc phạm vi.
        using var t = new TestDb();
        var session = TestDb.Session(Guid.NewGuid(), SessionStatus.Ready,
            deadline: DateTime.UtcNow.AddMinutes(-5));
        t.Db.Add(session);
        await t.Db.SaveChangesAsync();

        var (sweeper, pub) = Build(t);
        await ScanOnce(sweeper);

        pub.Verify(p => p.PublishSessionAbandonedAsync(
            It.IsAny<SessionAbandonedEvent>(), It.IsAny<CancellationToken>()), Times.Never);
    }

    [Fact]
    public async Task AlreadyAbandoned_NotRepublished()
    {
        using var t = new TestDb();
        var session = TestDb.Session(Guid.NewGuid(), SessionStatus.SessionAbandoned,
            deadline: DateTime.UtcNow.AddMinutes(-60));
        t.Db.Add(session);
        await t.Db.SaveChangesAsync();

        var (sweeper, pub) = Build(t);
        await ScanOnce(sweeper);

        pub.Verify(p => p.PublishSessionAbandonedAsync(
            It.IsAny<SessionAbandonedEvent>(), It.IsAny<CancellationToken>()), Times.Never);
    }

    // I2: session B2B quá Deadline + ≥1 answer → AUTO-SUBMIT (reuse SubmitSessionAsync). Câu chưa trả
    // lời → Skipped; mọi answer done (Scored + Skipped) → session Scored + phát SessionScored.
    [Fact]
    public async Task InProgress_PastDeadline_WithAnswer_AutoSubmits_SkipsUnanswered_AndCloses()
    {
        using var t = new TestDb();
        var candidate = Guid.NewGuid();
        var campaign = Guid.NewGuid();
        var session = TestDb.Session(candidate, SessionStatus.InProgress, campaignId: campaign,
            deadline: DateTime.UtcNow.AddMinutes(-1));
        var q1 = TestDb.Question(session.Id, 1);
        var q2 = TestDb.Question(session.Id, 2);
        // q1 đã trả lời + đã chấm xong (Scored); q2 CHƯA trả lời → chốt buổi sẽ đánh Skipped.
        var a1 = TestDb.Answer(session.Id, q1.Id, AnswerStatus.Scored,
            DateTime.UtcNow.AddMinutes(-30), DateTime.UtcNow.AddMinutes(-29));
        t.Db.AddRange(session, q1, q2, a1);
        await t.Db.SaveChangesAsync();

        var (sweeper, notifier) = BuildWithPractice(t);
        await ScanOnce(sweeper);

        var saved = await t.NewContext().PracticeSessions.AsNoTracking().FirstAsync(x => x.Id == session.Id);
        Assert.Equal(SessionStatus.Scored, saved.Status);
        Assert.NotNull(saved.CompletedAt);

        var answers = await t.NewContext().PracticeAnswers.AsNoTracking()
            .Where(x => x.SessionId == session.Id).ToListAsync();
        Assert.Equal(2, answers.Count);   // a1 (Scored) + a2 (Skipped) cho câu chưa trả lời
        Assert.Contains(answers, x => x.QuestionId == q2.Id && x.Status == AnswerStatus.Skipped);

        // E2: auto-submit đóng Scored → phát SessionScored (E7 nghe để consume credit).
        notifier.Verify(n => n.NotifySessionScoredAsync(session.Id, It.IsAny<CancellationToken>()), Times.Once);
    }

    // I2 auto-submit cần PracticeService thật (resolve từ scope). Đăng ký PracticeService + deps (mock)
    // vào cùng ServiceProvider mà sweeper dùng IServiceScopeFactory — chia chung SQLite connection.
    private static (SessionAbandonSweeper sweeper, Mock<ISessionScoringNotifier> notifier) BuildWithPractice(TestDb t)
    {
        var services = new ServiceCollection();
        services.AddLogging();
        services.AddDbContext<InterviewDbContext>(o => o.UseSqlite(t.Connection));

        var notifier = new Mock<ISessionScoringNotifier>();
        notifier.Setup(n => n.NotifySessionScoredAsync(It.IsAny<Guid>(), It.IsAny<CancellationToken>()))
                .Returns(Task.CompletedTask);

        var reservation = new Mock<ICreditReservationClient>();
        reservation.Setup(r => r.ReserveAsync(
                It.IsAny<string>(), It.IsAny<Guid>(), It.IsAny<Guid>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(new CreditReservationResult(Guid.NewGuid(), 1));

        services.AddSingleton(new Mock<IStorageService>().Object);
        services.AddSingleton(new Mock<IAiServiceQuestionGenerator>().Object);
        services.AddSingleton(notifier.Object);
        services.AddSingleton(reservation.Object);
        services.AddSingleton(new Mock<ISessionEventPublisher>().Object);
        services.AddScoped<IPracticeService, PracticeService>();

        var provider = services.BuildServiceProvider();

        var pub = new Mock<ISessionEventPublisher>();
        var sweeper = new SessionAbandonSweeper(
            provider.GetRequiredService<IServiceScopeFactory>(),
            pub.Object,
            NullLogger<SessionAbandonSweeper>.Instance);
        return (sweeper, notifier);
    }
}
