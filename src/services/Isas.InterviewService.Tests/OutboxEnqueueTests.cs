using Isas.InterviewService.Entities;
using Isas.InterviewService.Enums;
using Microsoft.EntityFrameworkCore;

namespace Isas.InterviewService.Tests;

// DB2 — SessionScoringNotifier ENQUEUE outbox-row settlement-event (SessionScored/SessionAbandoned)
// KHÔNG tự SaveChanges: caller commit CHUNG với state-flip (đóng session state + outbox-row atomic).
// Thay cho marker settlement_published_at + publish best-effort cũ. Payload lưu nguyên (không reconstruct).
public class OutboxEnqueueTests
{
    // ── EnqueueSessionScoredAsync ─────────────────────────────────────────

    [Fact]
    public async Task EnqueueScored_AddsScoredRow_OnlyPersistedAfterCallerSaves()
    {
        using var t = new TestDb();
        var candidate = Guid.NewGuid();
        var session = TestDb.Session(candidate, SessionStatus.Scored, JobCategory.BE);
        t.Db.Add(session);
        await t.Db.SaveChangesAsync();

        await TestDb.Notifier(t.Db).EnqueueSessionScoredAsync(session.Id);

        // Enqueue KHÔNG save → row chỉ ở change-tracker, DB (đọc qua context khác) chưa có.
        Assert.Equal(0, await t.NewContext().OutboxMessages.CountAsync());

        await t.Db.SaveChangesAsync();   // caller commit (atomic với state-flip trong luồng thật)

        var saved = TestDb.ScoredOutbox(t.NewContext(), session.Id);
        Assert.NotNull(saved);
        Assert.Equal(session.Id, saved!.SessionId);
        Assert.Null(saved.CampaignId);            // B2C
        Assert.Equal(candidate, saved.CandidateId);
    }

    [Fact]
    public async Task EnqueueScored_RowIsUnpublished_WithStableMessageId()
    {
        using var t = new TestDb();
        var session = TestDb.Session(Guid.NewGuid(), SessionStatus.Scored, JobCategory.BE);
        t.Db.Add(session);
        await t.Db.SaveChangesAsync();

        await TestDb.Notifier(t.Db).EnqueueSessionScoredAsync(session.Id);
        await t.Db.SaveChangesAsync();

        var row = await t.NewContext().OutboxMessages.AsNoTracking()
            .SingleAsync(m => m.SessionId == session.Id);
        Assert.Equal(OutboxMessage.SessionScoredType, row.Type);   // routing key
        Assert.Null(row.PublishedAt);                              // chưa publish (dispatcher lo)
        Assert.Equal(0, row.Attempts);
        Assert.NotEqual(Guid.Empty, row.Id);                       // message-id ổn định
    }

    // ── EnqueueSessionAbandonedAsync (PAY-13 / generation_failed) ──────────

    [Fact]
    public async Task EnqueueAbandoned_AddsAbandonedRow_WithReasonPreserved()
    {
        using var t = new TestDb();
        var candidate = Guid.NewGuid();
        var session = TestDb.Session(candidate, SessionStatus.SessionAbandoned, JobCategory.BE);
        t.Db.Add(session);
        await t.Db.SaveChangesAsync();

        await TestDb.Notifier(t.Db).EnqueueSessionAbandonedAsync(session.Id, "no_scored_answer");
        await t.Db.SaveChangesAsync();

        var saved = TestDb.AbandonedOutbox(t.NewContext(), session.Id);
        Assert.NotNull(saved);
        Assert.Equal(session.Id, saved!.SessionId);
        Assert.Equal(candidate, saved.CandidateId);
        Assert.Equal("no_scored_answer", saved.Reason);   // reason GIỮ NGUYÊN (không suy biến)
    }

    [Fact]
    public async Task EnqueueAbandoned_RowIsUnpublished_WithStableMessageId()
    {
        using var t = new TestDb();
        var session = TestDb.Session(Guid.NewGuid(), SessionStatus.SessionAbandoned, JobCategory.BE);
        t.Db.Add(session);
        await t.Db.SaveChangesAsync();

        await TestDb.Notifier(t.Db).EnqueueSessionAbandonedAsync(session.Id, "no_scored_answer");
        await t.Db.SaveChangesAsync();

        var row = await t.NewContext().OutboxMessages.AsNoTracking()
            .SingleAsync(m => m.SessionId == session.Id);
        Assert.Equal(OutboxMessage.SessionAbandonedType, row.Type);
        Assert.Null(row.PublishedAt);
        Assert.Equal(0, row.Attempts);
        Assert.NotEqual(Guid.Empty, row.Id);
    }

    [Fact]
    public async Task EnqueueAbandoned_PreservesGenerationFailedReason()
    {
        using var t = new TestDb();
        var session = TestDb.Session(Guid.NewGuid(), SessionStatus.Failed, JobCategory.BE);
        t.Db.Add(session);
        await t.Db.SaveChangesAsync();

        await TestDb.Notifier(t.Db).EnqueueSessionAbandonedAsync(session.Id, "generation_failed");
        await t.Db.SaveChangesAsync();

        var saved = TestDb.AbandonedOutbox(t.NewContext(), session.Id);
        Assert.Equal("generation_failed", saved!.Reason);
    }

    [Fact]
    public async Task Enqueue_UnknownSession_NoRow()
    {
        using var t = new TestDb();
        await TestDb.Notifier(t.Db).EnqueueSessionScoredAsync(Guid.NewGuid());
        await t.Db.SaveChangesAsync();
        Assert.Equal(0, await t.NewContext().OutboxMessages.CountAsync());
    }

    // B2B: payload GIỮ TotalScore weighted (không suy biến) — SettlementReconciler cũ bỏ sót B2B.
    [Fact]
    public async Task EnqueueScored_B2B_PayloadCarriesWeightedTotalScore()
    {
        using var t = new TestDb();
        var candidate = Guid.NewGuid();
        var campaign = Guid.NewGuid();
        var session = TestDb.Session(candidate, SessionStatus.Scored, JobCategory.BE, campaignId: campaign);
        var q = TestDb.Question(session.Id);
        // Tiêu chí campaign (B2B) maxScore 5, weight 1.0 → score 4 → 80%.
        var crit = TestDb.Criterion(JobCategory.BE, campaignId: campaign);
        crit.MaxScore = 5; crit.Weight = 1.0m;
        var answer = TestDb.Answer(session.Id, q.Id, AnswerStatus.Scored, DateTime.UtcNow, DateTime.UtcNow);
        t.Db.AddRange(session, q, crit, answer);
        await t.Db.SaveChangesAsync();
        t.Db.AnswerScores.Add(new AnswerScore
        {
            Id = Guid.NewGuid(), AnswerId = answer.Id, CriterionId = crit.Id,
            AttemptNo = 1, Score = 4m, Reasoning = "ok", RubricVersion = 1, CreatedAt = DateTime.UtcNow
        });
        await t.Db.SaveChangesAsync();

        await TestDb.Notifier(t.Db).EnqueueSessionScoredAsync(session.Id);
        await t.Db.SaveChangesAsync();

        var saved = TestDb.ScoredOutbox(t.NewContext(), session.Id);
        Assert.Equal(campaign, saved!.CampaignId);
        Assert.Equal(80m, saved.TotalScore);   // weighted, không suy biến
    }
}
