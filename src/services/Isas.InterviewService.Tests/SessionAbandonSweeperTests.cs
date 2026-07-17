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
    // StuckAnswerRepublisherTests.Build). P1-1: b2cInactivityMinutes tùy chỉnh ngưỡng quét B2C.
    private static (SessionAbandonSweeper sweeper, Mock<ISessionEventPublisher> pub) Build(
        TestDb t, int b2cInactivityMinutes = 120)
    {
        var services = new ServiceCollection();
        services.AddDbContext<InterviewDbContext>(o => o.UseSqlite(t.Connection).UseSnakeCaseNamingConvention());
        var provider = services.BuildServiceProvider();

        var pub = new Mock<ISessionEventPublisher>();
        var sweeper = new SessionAbandonSweeper(
            provider.GetRequiredService<IServiceScopeFactory>(),
            pub.Object,
            Options.Create(new ScoringOptions { B2CInactivityMinutes = b2cInactivityMinutes }),
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
    public async Task InProgress_NullDeadline_ActiveWithRecentAnswer_NotTouched()
    {
        // I2: B2C (Deadline null) → KHÔNG hard-deadline → nhánh deadline-sweep bỏ qua (không auto-submit).
        // P1-1: session còn HOẠT ĐỘNG (answer vừa upload trong ngưỡng) → nhánh inactivity cũng bỏ qua.
        using var t = new TestDb();
        var session = TestDb.Session(Guid.NewGuid(), SessionStatus.InProgress,
            createdAt: DateTime.UtcNow.AddMinutes(-10), deadline: null);
        var q = TestDb.Question(session.Id);
        var a = TestDb.Answer(session.Id, q.Id, AnswerStatus.Uploaded,
            DateTime.UtcNow.AddMinutes(-3), lastPublished: null);
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
        services.AddDbContext<InterviewDbContext>(o => o.UseSqlite(t.Connection).UseSnakeCaseNamingConvention());

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
            Options.Create(new ScoringOptions()),
            NullLogger<SessionAbandonSweeper>.Instance);
        return (sweeper, notifier);
    }

    // P1-1: session B2C (Deadline null, CampaignId null) Ready & không hoạt động quá ngưỡng → bỏ ngang +
    // phát SessionAbandoned (reason=inactivity_timeout) để Payment release credit ví User.
    [Fact]
    public async Task InactiveB2C_Ready_PastThreshold_Abandoned_AndPublishesEvent()
    {
        using var t = new TestDb();
        var candidate = Guid.NewGuid();
        // B2C: campaignId null + deadline null; CreatedAt cũ hơn ngưỡng 120' → không hoạt động.
        var session = TestDb.Session(candidate, SessionStatus.Ready,
            createdAt: DateTime.UtcNow.AddMinutes(-121), deadline: null);
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
        Assert.Equal(candidate, published.CandidateId);
        Assert.Null(published.CampaignId);                 // B2C
        Assert.Equal("inactivity_timeout", published.Reason);

        var saved = await t.NewContext().PracticeSessions.AsNoTracking().FirstAsync(x => x.Id == session.Id);
        Assert.Equal(SessionStatus.SessionAbandoned, saved.Status);
        Assert.NotNull(saved.CompletedAt);
    }

    // Settlement-outbox COMMIT 1: sweeper phát SessionAbandoned OK → set settlement_published_at
    // (điểm phát thứ 3). SettlementReconciler dựa vào marker này để bỏ qua session đã phát.
    [Fact]
    public async Task InactiveB2C_AbandonPublishSuccess_SetsSettlementMarker()
    {
        using var t = new TestDb();
        var session = TestDb.Session(Guid.NewGuid(), SessionStatus.Ready,
            createdAt: DateTime.UtcNow.AddMinutes(-121), deadline: null);
        t.Db.Add(session);
        await t.Db.SaveChangesAsync();

        var (sweeper, pub) = Build(t);
        pub.Setup(p => p.PublishSessionAbandonedAsync(It.IsAny<SessionAbandonedEvent>(), It.IsAny<CancellationToken>()))
           .Returns(Task.CompletedTask);

        await ScanOnce(sweeper);

        var saved = await t.NewContext().PracticeSessions.AsNoTracking().FirstAsync(x => x.Id == session.Id);
        Assert.Equal(SessionStatus.SessionAbandoned, saved.Status);
        Assert.NotNull(saved.SettlementPublishedAt);
    }

    // Settlement-outbox: sweeper phát HỤT → marker giữ null (reconciler sẽ backfill), không ném ra ngoài.
    [Fact]
    public async Task InactiveB2C_AbandonPublishThrows_MarkerStaysNull()
    {
        using var t = new TestDb();
        var session = TestDb.Session(Guid.NewGuid(), SessionStatus.Ready,
            createdAt: DateTime.UtcNow.AddMinutes(-121), deadline: null);
        t.Db.Add(session);
        await t.Db.SaveChangesAsync();

        var (sweeper, pub) = Build(t);
        pub.Setup(p => p.PublishSessionAbandonedAsync(It.IsAny<SessionAbandonedEvent>(), It.IsAny<CancellationToken>()))
           .ThrowsAsync(new Exception("bus down"));

        await ScanOnce(sweeper);   // best-effort: không ném ra ngoài

        var saved = await t.NewContext().PracticeSessions.AsNoTracking().FirstAsync(x => x.Id == session.Id);
        Assert.Equal(SessionStatus.SessionAbandoned, saved.Status);   // vẫn đóng session trong DB
        Assert.Null(saved.SettlementPublishedAt);
    }

    // P1-1: session B2C InProgress không hoạt động quá ngưỡng (0 answer) → cũng bỏ ngang.
    [Fact]
    public async Task InactiveB2C_InProgress_PastThreshold_Abandoned()
    {
        using var t = new TestDb();
        var candidate = Guid.NewGuid();
        var session = TestDb.Session(candidate, SessionStatus.InProgress,
            createdAt: DateTime.UtcNow.AddMinutes(-200), deadline: null);
        t.Db.Add(session);
        await t.Db.SaveChangesAsync();

        var (sweeper, pub) = Build(t);
        await ScanOnce(sweeper);

        pub.Verify(p => p.PublishSessionAbandonedAsync(
            It.IsAny<SessionAbandonedEvent>(), It.IsAny<CancellationToken>()), Times.Once);
        var saved = await t.NewContext().PracticeSessions.AsNoTracking().FirstAsync(x => x.Id == session.Id);
        Assert.Equal(SessionStatus.SessionAbandoned, saved.Status);
    }

    // P1-1 (money-correctness): session B2C tạo lâu NHƯNG người đang luyện (vừa upload answer trong
    // ngưỡng) → last-activity mới → KHÔNG bị quét. Bảo vệ người đang thao tác.
    [Fact]
    public async Task InactiveB2C_OldSession_ButRecentAnswer_NotAbandoned()
    {
        using var t = new TestDb();
        var candidate = Guid.NewGuid();
        var session = TestDb.Session(candidate, SessionStatus.InProgress,
            createdAt: DateTime.UtcNow.AddMinutes(-200), deadline: null);
        var q = TestDb.Question(session.Id);
        // answer upload GẦN ĐÂY (trong ngưỡng 120') → đang hoạt động.
        var a = TestDb.Answer(session.Id, q.Id, AnswerStatus.Uploaded,
            DateTime.UtcNow.AddMinutes(-3), lastPublished: DateTime.UtcNow.AddMinutes(-3));
        t.Db.AddRange(session, q, a);
        await t.Db.SaveChangesAsync();

        var (sweeper, pub) = Build(t);
        await ScanOnce(sweeper);

        pub.Verify(p => p.PublishSessionAbandonedAsync(
            It.IsAny<SessionAbandonedEvent>(), It.IsAny<CancellationToken>()), Times.Never);
        var saved = await t.NewContext().PracticeSessions.AsNoTracking().FirstAsync(x => x.Id == session.Id);
        Assert.Equal(SessionStatus.InProgress, saved.Status);
    }

    // P1-1: session B2C mới tạo (trong ngưỡng) → KHÔNG bị quét.
    [Fact]
    public async Task InactiveB2C_FreshSession_NotAbandoned()
    {
        using var t = new TestDb();
        var session = TestDb.Session(Guid.NewGuid(), SessionStatus.Ready,
            createdAt: DateTime.UtcNow.AddMinutes(-5), deadline: null);
        t.Db.Add(session);
        await t.Db.SaveChangesAsync();

        var (sweeper, pub) = Build(t);
        await ScanOnce(sweeper);

        pub.Verify(p => p.PublishSessionAbandonedAsync(
            It.IsAny<SessionAbandonedEvent>(), It.IsAny<CancellationToken>()), Times.Never);
        var saved = await t.NewContext().PracticeSessions.AsNoTracking().FirstAsync(x => x.Id == session.Id);
        Assert.Equal(SessionStatus.Ready, saved.Status);
    }

    // P1-1: B2B với Deadline null (Campaign chưa gửi expires_at) KHÔNG bị nhánh B2C quét (CampaignId!=null).
    // B2B behavior giữ nguyên: chỉ bị đóng qua nhánh Deadline (không có ở đây).
    [Fact]
    public async Task B2B_NullDeadline_OldSession_NotSweptByB2CBranch()
    {
        using var t = new TestDb();
        var session = TestDb.Session(Guid.NewGuid(), SessionStatus.InProgress,
            campaignId: Guid.NewGuid(),                    // B2B
            createdAt: DateTime.UtcNow.AddMinutes(-300), deadline: null);
        t.Db.Add(session);
        await t.Db.SaveChangesAsync();

        var (sweeper, pub) = Build(t);
        await ScanOnce(sweeper);

        pub.Verify(p => p.PublishSessionAbandonedAsync(
            It.IsAny<SessionAbandonedEvent>(), It.IsAny<CancellationToken>()), Times.Never);
        var saved = await t.NewContext().PracticeSessions.AsNoTracking().FirstAsync(x => x.Id == session.Id);
        Assert.Equal(SessionStatus.InProgress, saved.Status);
    }
}
