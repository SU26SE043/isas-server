using System.Reflection;
using Isas.InterviewService.ApplicationDbContext;
using Isas.InterviewService.DTOs;
using Isas.InterviewService.Entities;
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

// Settlement-outbox (Option A) COMMIT 2 — SettlementReconciler phát lại settlement-event cho session
// B2C terminal (Scored/SessionAbandoned) mà settlement_published_at còn null (publish hụt lúc đóng
// session), rồi set marker (idempotent). CHỈ B2C (CampaignId == null): B2B out-of-scope (session.scored
// nuôi ranking E4 bằng TotalScore không lưu DB).
public class SettlementReconcilerTests
{
    // Gọi ScanOnceAsync (private) một nhịp — giống StuckAnswerRepublisherTests/SessionAbandonSweeperTests.
    private static async Task ScanOnce(SettlementReconciler r)
    {
        var mi = typeof(SettlementReconciler)
            .GetMethod("ScanOnceAsync", BindingFlags.NonPublic | BindingFlags.Instance)!;
        await (Task)mi.Invoke(r, new object[] { CancellationToken.None })!;
    }

    // ServiceProvider thật để CreateScope() trả về DbContext dùng chung connection (giống các sweeper test).
    private static (SettlementReconciler r, Mock<ISessionEventPublisher> pub) Build(
        TestDb t, int graceMinutes = 2, int lookbackHours = 24)
    {
        var services = new ServiceCollection();
        services.AddDbContext<InterviewDbContext>(o => o.UseSqlite(t.Connection));
        var provider = services.BuildServiceProvider();

        var pub = new Mock<ISessionEventPublisher>();
        var r = new SettlementReconciler(
            provider.GetRequiredService<IServiceScopeFactory>(),
            pub.Object,
            Options.Create(new ScoringOptions
            {
                SettlementRepublishGraceMinutes = graceMinutes,
                SettlementRepublishLookbackHours = lookbackHours
            }),
            NullLogger<SettlementReconciler>.Instance);
        return (r, pub);
    }

    // B2C terminal chưa phát: set CompletedAt (đóng session) + để SettlementPublishedAt null.
    private static PracticeSession UnpublishedB2C(
        SessionStatus status, DateTime completedAt, decimal? overallScore = null, Guid? candidate = null)
    {
        var s = TestDb.Session(candidate ?? Guid.NewGuid(), status, JobCategory.BE);
        s.CompletedAt = completedAt;
        s.OverallScore = overallScore;
        s.SettlementPublishedAt = null;
        return s;
    }

    // (1) B2C Scored quá grace, marker null → PublishSessionScoredAsync đúng field + set marker.
    [Fact]
    public async Task B2CScored_PastGrace_PublishesSessionScored_AndSetsMarker()
    {
        using var t = new TestDb();
        var candidate = Guid.NewGuid();
        var completedAt = DateTime.UtcNow.AddMinutes(-10);
        var session = UnpublishedB2C(SessionStatus.Scored, completedAt, overallScore: 72m, candidate: candidate);
        t.Db.Add(session);
        await t.Db.SaveChangesAsync();

        var (r, pub) = Build(t);
        SessionScoredEvent? published = null;
        pub.Setup(p => p.PublishSessionScoredAsync(It.IsAny<SessionScoredEvent>(), It.IsAny<CancellationToken>()))
           .Callback<SessionScoredEvent, CancellationToken>((e, _) => published = e)
           .Returns(Task.CompletedTask);

        await ScanOnce(r);

        pub.Verify(p => p.PublishSessionScoredAsync(
            It.IsAny<SessionScoredEvent>(), It.IsAny<CancellationToken>()), Times.Once);
        Assert.NotNull(published);
        Assert.Equal(session.Id, published!.SessionId);
        Assert.Null(published.CampaignId);                  // B2C
        Assert.Equal(candidate, published.CandidateId);
        Assert.Equal(72m, published.TotalScore);            // OverallScore snapshot
        Assert.Equal(completedAt, published.ScoredAt);      // = CompletedAt

        var saved = await t.NewContext().PracticeSessions.AsNoTracking().FirstAsync(x => x.Id == session.Id);
        Assert.NotNull(saved.SettlementPublishedAt);        // marker set → vòng sau bỏ qua
    }

    // OverallScore null → TotalScore = 0 (Payment không dùng field này; chỉ tránh null).
    [Fact]
    public async Task B2CScored_NullOverallScore_PublishesZeroTotal()
    {
        using var t = new TestDb();
        var session = UnpublishedB2C(SessionStatus.Scored, DateTime.UtcNow.AddMinutes(-10), overallScore: null);
        t.Db.Add(session);
        await t.Db.SaveChangesAsync();

        var (r, pub) = Build(t);
        SessionScoredEvent? published = null;
        pub.Setup(p => p.PublishSessionScoredAsync(It.IsAny<SessionScoredEvent>(), It.IsAny<CancellationToken>()))
           .Callback<SessionScoredEvent, CancellationToken>((e, _) => published = e)
           .Returns(Task.CompletedTask);

        await ScanOnce(r);

        Assert.NotNull(published);
        Assert.Equal(0m, published!.TotalScore);
    }

    // (2) B2C SessionAbandoned quá grace → PublishSessionAbandonedAsync (reason=reconciled) + set marker.
    [Fact]
    public async Task B2CAbandoned_PastGrace_PublishesSessionAbandoned_AndSetsMarker()
    {
        using var t = new TestDb();
        var candidate = Guid.NewGuid();
        var completedAt = DateTime.UtcNow.AddMinutes(-10);
        var session = UnpublishedB2C(SessionStatus.SessionAbandoned, completedAt, candidate: candidate);
        t.Db.Add(session);
        await t.Db.SaveChangesAsync();

        var (r, pub) = Build(t);
        SessionAbandonedEvent? published = null;
        pub.Setup(p => p.PublishSessionAbandonedAsync(It.IsAny<SessionAbandonedEvent>(), It.IsAny<CancellationToken>()))
           .Callback<SessionAbandonedEvent, CancellationToken>((e, _) => published = e)
           .Returns(Task.CompletedTask);

        await ScanOnce(r);

        pub.Verify(p => p.PublishSessionAbandonedAsync(
            It.IsAny<SessionAbandonedEvent>(), It.IsAny<CancellationToken>()), Times.Once);
        Assert.NotNull(published);
        Assert.Equal(session.Id, published!.SessionId);
        Assert.Null(published.CampaignId);
        Assert.Equal(candidate, published.CandidateId);
        Assert.Equal("reconciled", published.Reason);
        Assert.Equal(completedAt, published.AbandonedAt);

        var saved = await t.NewContext().PracticeSessions.AsNoTracking().FirstAsync(x => x.Id == session.Id);
        Assert.NotNull(saved.SettlementPublishedAt);
    }

    // SKIPPED: đóng session còn trong grace (CompletedAt quá gần now) → chưa phát lại.
    [Fact]
    public async Task WithinGrace_NotRepublished()
    {
        using var t = new TestDb();
        var session = UnpublishedB2C(SessionStatus.Scored, DateTime.UtcNow.AddSeconds(-30));  // < 2' grace
        t.Db.Add(session);
        await t.Db.SaveChangesAsync();

        var (r, pub) = Build(t);
        await ScanOnce(r);

        pub.Verify(p => p.PublishSessionScoredAsync(
            It.IsAny<SessionScoredEvent>(), It.IsAny<CancellationToken>()), Times.Never);
        var saved = await t.NewContext().PracticeSessions.AsNoTracking().FirstAsync(x => x.Id == session.Id);
        Assert.Null(saved.SettlementPublishedAt);
    }

    // SKIPPED: marker đã set (đã phát ở đường-chính) → không phát lại.
    [Fact]
    public async Task MarkerAlreadySet_NotRepublished()
    {
        using var t = new TestDb();
        var session = UnpublishedB2C(SessionStatus.Scored, DateTime.UtcNow.AddMinutes(-10));
        session.SettlementPublishedAt = DateTime.UtcNow.AddMinutes(-9);   // đã phát rồi
        t.Db.Add(session);
        await t.Db.SaveChangesAsync();

        var (r, pub) = Build(t);
        await ScanOnce(r);

        pub.Verify(p => p.PublishSessionScoredAsync(
            It.IsAny<SessionScoredEvent>(), It.IsAny<CancellationToken>()), Times.Never);
    }

    // SKIPPED: B2B (CampaignId != null) — out-of-scope (ranking E4 dùng TotalScore không lưu DB).
    [Fact]
    public async Task B2BSession_NotRepublished()
    {
        using var t = new TestDb();
        var session = UnpublishedB2C(SessionStatus.Scored, DateTime.UtcNow.AddMinutes(-10));
        session.CampaignId = Guid.NewGuid();   // B2B
        t.Db.Add(session);
        await t.Db.SaveChangesAsync();

        var (r, pub) = Build(t);
        await ScanOnce(r);

        pub.Verify(p => p.PublishSessionScoredAsync(
            It.IsAny<SessionScoredEvent>(), It.IsAny<CancellationToken>()), Times.Never);
        var saved = await t.NewContext().PracticeSessions.AsNoTracking().FirstAsync(x => x.Id == session.Id);
        Assert.Null(saved.SettlementPublishedAt);   // không đụng B2B
    }

    // SKIPPED: đóng session quá cũ (ngoài lookback) → không "hồi sinh" event cũ.
    [Fact]
    public async Task OutsideLookback_NotRepublished()
    {
        using var t = new TestDb();
        var session = UnpublishedB2C(SessionStatus.Scored, DateTime.UtcNow.AddHours(-25));  // > 24h lookback
        t.Db.Add(session);
        await t.Db.SaveChangesAsync();

        var (r, pub) = Build(t);
        await ScanOnce(r);

        pub.Verify(p => p.PublishSessionScoredAsync(
            It.IsAny<SessionScoredEvent>(), It.IsAny<CancellationToken>()), Times.Never);
    }

    // SKIPPED: session chưa terminal (InProgress) → không phát settlement.
    [Fact]
    public async Task NonTerminalStatus_NotRepublished()
    {
        using var t = new TestDb();
        var session = UnpublishedB2C(SessionStatus.InProgress, DateTime.UtcNow.AddMinutes(-10));
        t.Db.Add(session);
        await t.Db.SaveChangesAsync();

        var (r, pub) = Build(t);
        await ScanOnce(r);

        pub.Verify(p => p.PublishSessionScoredAsync(
            It.IsAny<SessionScoredEvent>(), It.IsAny<CancellationToken>()), Times.Never);
        pub.Verify(p => p.PublishSessionAbandonedAsync(
            It.IsAny<SessionAbandonedEvent>(), It.IsAny<CancellationToken>()), Times.Never);
    }

    // Publish HỤT → marker giữ null (vòng sau thử lại), không ném ra ngoài.
    [Fact]
    public async Task PublishThrows_MarkerStaysNull_WouldRetry()
    {
        using var t = new TestDb();
        var session = UnpublishedB2C(SessionStatus.Scored, DateTime.UtcNow.AddMinutes(-10));
        t.Db.Add(session);
        await t.Db.SaveChangesAsync();

        var (r, pub) = Build(t);
        pub.Setup(p => p.PublishSessionScoredAsync(It.IsAny<SessionScoredEvent>(), It.IsAny<CancellationToken>()))
           .ThrowsAsync(new Exception("bus down"));

        await ScanOnce(r);   // không được ném ra ngoài

        var saved = await t.NewContext().PracticeSessions.AsNoTracking().FirstAsync(x => x.Id == session.Id);
        Assert.Null(saved.SettlementPublishedAt);   // vòng sau phát lại
    }

    // Idempotent: sau khi phát + set marker, quét lần 2 KHÔNG phát lại.
    [Fact]
    public async Task Idempotent_SecondScan_DoesNotRepublish()
    {
        using var t = new TestDb();
        var session = UnpublishedB2C(SessionStatus.Scored, DateTime.UtcNow.AddMinutes(-10), overallScore: 50m);
        t.Db.Add(session);
        await t.Db.SaveChangesAsync();

        var (r, pub) = Build(t);
        pub.Setup(p => p.PublishSessionScoredAsync(It.IsAny<SessionScoredEvent>(), It.IsAny<CancellationToken>()))
           .Returns(Task.CompletedTask);

        await ScanOnce(r);
        await ScanOnce(r);   // marker đã set ở vòng 1 → vòng 2 bỏ qua

        pub.Verify(p => p.PublishSessionScoredAsync(
            It.IsAny<SessionScoredEvent>(), It.IsAny<CancellationToken>()), Times.Once);
    }

    // Safe-disable: grace <= 0 → reconciler không làm gì (không phát, không set marker).
    [Fact]
    public async Task GraceDisabled_DoesNothing()
    {
        using var t = new TestDb();
        var session = UnpublishedB2C(SessionStatus.Scored, DateTime.UtcNow.AddMinutes(-10));
        t.Db.Add(session);
        await t.Db.SaveChangesAsync();

        var (r, pub) = Build(t, graceMinutes: 0);
        await ScanOnce(r);

        pub.Verify(p => p.PublishSessionScoredAsync(
            It.IsAny<SessionScoredEvent>(), It.IsAny<CancellationToken>()), Times.Never);
        var saved = await t.NewContext().PracticeSessions.AsNoTracking().FirstAsync(x => x.Id == session.Id);
        Assert.Null(saved.SettlementPublishedAt);
    }
}
