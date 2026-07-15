using Isas.InterviewService.DTOs;
using Isas.InterviewService.Enums;
using Isas.InterviewService.Services;
using Isas.InterviewService.Services.Interfaces;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging.Abstractions;
using Moq;

namespace Isas.InterviewService.Tests;

// Settlement-outbox (Option A) COMMIT 1 — mốc settlement_published_at set khi PHÁT THÀNH CÔNG event
// settlement (SessionScored/SessionAbandoned), giữ null khi publish HỤT (log-and-swallow best-effort).
// Đây là marker SettlementReconciler dựa vào để backfill session terminal chưa phát được (đóng lỗ
// "publish hụt → Payment giữ reservation Reserved vĩnh viễn").
public class SettlementMarkerTests
{
    // Notifier THẬT + result/roadmap THẬT; chỉ mock transport event (có thể set để ném để test publish hụt).
    private static SessionScoringNotifier BuildNotifier(TestDb t, Mock<ISessionEventPublisher> eventPub)
        => new(t.Db, eventPub.Object, TestDb.ResultService(t.Db), TestDb.Summarizer(),
               TestDb.RoadmapReport(t.Db), NullLogger<SessionScoringNotifier>.Instance);

    // ── NotifySessionScoredAsync ──────────────────────────────────────────

    [Fact]
    public async Task Scored_PublishSuccess_SetsMarker()
    {
        using var t = new TestDb();
        var session = TestDb.Session(Guid.NewGuid(), SessionStatus.Scored, JobCategory.BE);
        t.Db.Add(session);
        await t.Db.SaveChangesAsync();

        var pub = new Mock<ISessionEventPublisher>();
        pub.Setup(p => p.PublishSessionScoredAsync(It.IsAny<SessionScoredEvent>(), It.IsAny<CancellationToken>()))
           .Returns(Task.CompletedTask);

        await BuildNotifier(t, pub).NotifySessionScoredAsync(session.Id);

        var saved = await t.NewContext().PracticeSessions.AsNoTracking().FirstAsync(x => x.Id == session.Id);
        Assert.NotNull(saved.SettlementPublishedAt);
        Assert.True(saved.SettlementPublishedAt > DateTime.UtcNow.AddMinutes(-1));
    }

    [Fact]
    public async Task Scored_PublishThrows_MarkerStaysNull_NoException()
    {
        using var t = new TestDb();
        var session = TestDb.Session(Guid.NewGuid(), SessionStatus.Scored, JobCategory.BE);
        t.Db.Add(session);
        await t.Db.SaveChangesAsync();

        var pub = new Mock<ISessionEventPublisher>();
        pub.Setup(p => p.PublishSessionScoredAsync(It.IsAny<SessionScoredEvent>(), It.IsAny<CancellationToken>()))
           .ThrowsAsync(new Exception("bus down"));

        // Best-effort: publish hụt KHÔNG được ném ra ngoài notifier.
        await BuildNotifier(t, pub).NotifySessionScoredAsync(session.Id);

        var saved = await t.NewContext().PracticeSessions.AsNoTracking().FirstAsync(x => x.Id == session.Id);
        Assert.Null(saved.SettlementPublishedAt);   // marker null → reconciler sẽ phát lại
    }

    // ── NotifySessionAbandonedAsync (PAY-13: session đóng, 0 answer Scored) ─

    [Fact]
    public async Task Abandoned_PublishSuccess_SetsMarker()
    {
        using var t = new TestDb();
        var session = TestDb.Session(Guid.NewGuid(), SessionStatus.SessionAbandoned, JobCategory.BE);
        t.Db.Add(session);
        await t.Db.SaveChangesAsync();

        var pub = new Mock<ISessionEventPublisher>();
        pub.Setup(p => p.PublishSessionAbandonedAsync(It.IsAny<SessionAbandonedEvent>(), It.IsAny<CancellationToken>()))
           .Returns(Task.CompletedTask);

        await BuildNotifier(t, pub).NotifySessionAbandonedAsync(session.Id, "no_scored_answer");

        var saved = await t.NewContext().PracticeSessions.AsNoTracking().FirstAsync(x => x.Id == session.Id);
        Assert.NotNull(saved.SettlementPublishedAt);
    }

    [Fact]
    public async Task Abandoned_PublishThrows_MarkerStaysNull_NoException()
    {
        using var t = new TestDb();
        var session = TestDb.Session(Guid.NewGuid(), SessionStatus.SessionAbandoned, JobCategory.BE);
        t.Db.Add(session);
        await t.Db.SaveChangesAsync();

        var pub = new Mock<ISessionEventPublisher>();
        pub.Setup(p => p.PublishSessionAbandonedAsync(It.IsAny<SessionAbandonedEvent>(), It.IsAny<CancellationToken>()))
           .ThrowsAsync(new Exception("bus down"));

        await BuildNotifier(t, pub).NotifySessionAbandonedAsync(session.Id, "no_scored_answer");

        var saved = await t.NewContext().PracticeSessions.AsNoTracking().FirstAsync(x => x.Id == session.Id);
        Assert.Null(saved.SettlementPublishedAt);
    }
}
