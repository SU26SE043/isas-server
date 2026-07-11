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
        // InProgress, tạo 40' trước, KHÔNG có answer nào -> quá ngưỡng 30' -> bỏ ngang.
        var session = TestDb.Session(candidateId, SessionStatus.InProgress, campaignId: campaignId,
            createdAt: DateTime.UtcNow.AddMinutes(-40));
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
    public async Task InProgress_PastDeadline_WithAnswer_NotAbandoned()
    {
        // ≥1 answer -> nhánh auto-submit (I2), KHÔNG thuộc phạm vi E3 -> sweeper bỏ qua.
        using var t = new TestDb();
        var session = TestDb.Session(Guid.NewGuid(), SessionStatus.InProgress,
            createdAt: DateTime.UtcNow.AddMinutes(-40));
        var q = TestDb.Question(session.Id);
        var a = TestDb.Answer(session.Id, q.Id, AnswerStatus.Uploaded,
            DateTime.UtcNow.AddMinutes(-40), lastPublished: null);
        t.Db.AddRange(session, q, a);
        await t.Db.SaveChangesAsync();

        var (sweeper, pub) = Build(t);
        await ScanOnce(sweeper);

        pub.Verify(p => p.PublishSessionAbandonedAsync(
            It.IsAny<SessionAbandonedEvent>(), It.IsAny<CancellationToken>()), Times.Never);

        var saved = await t.NewContext().PracticeSessions.AsNoTracking().FirstAsync(x => x.Id == session.Id);
        Assert.Equal(SessionStatus.InProgress, saved.Status);
    }

    [Fact]
    public async Task InProgress_WithinDeadline_ZeroAnswers_NotAbandoned()
    {
        using var t = new TestDb();
        // Mới tạo 5' trước -> còn trong hạn 30'.
        var session = TestDb.Session(Guid.NewGuid(), SessionStatus.InProgress,
            createdAt: DateTime.UtcNow.AddMinutes(-5));
        t.Db.Add(session);
        await t.Db.SaveChangesAsync();

        var (sweeper, pub) = Build(t);
        await ScanOnce(sweeper);

        pub.Verify(p => p.PublishSessionAbandonedAsync(
            It.IsAny<SessionAbandonedEvent>(), It.IsAny<CancellationToken>()), Times.Never);
    }

    [Fact]
    public async Task ReadyStatus_PastDeadline_ZeroAnswers_NotAbandoned()
    {
        // Task pin theo InProgress (tasks.md E3) — Ready (chưa bắt đầu) quá hạn KHÔNG thuộc
        // phạm vi sweeper này.
        using var t = new TestDb();
        var session = TestDb.Session(Guid.NewGuid(), SessionStatus.Ready,
            createdAt: DateTime.UtcNow.AddMinutes(-60));
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
            createdAt: DateTime.UtcNow.AddMinutes(-60));
        t.Db.Add(session);
        await t.Db.SaveChangesAsync();

        var (sweeper, pub) = Build(t);
        await ScanOnce(sweeper);

        pub.Verify(p => p.PublishSessionAbandonedAsync(
            It.IsAny<SessionAbandonedEvent>(), It.IsAny<CancellationToken>()), Times.Never);
    }
}
