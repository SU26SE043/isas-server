using Isas.InterviewService.DTOs;
using Isas.InterviewService.Entities;
using Isas.InterviewService.Enums;
using Isas.InterviewService.Models;
using Isas.InterviewService.Services;
using Isas.InterviewService.Services.Interfaces;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using Moq;
using Xunit;

namespace Isas.InterviewService.Tests;

/// <summary>
/// RNK1 · HĐ-5 — <c>rubric_criteria.source_criterion_id</c> đi từ payload materialize (Campaign gửi
/// <c>criterionId</c>) → cột trên rubric → <c>CriterionInputSnapshot.CriterionId</c> của event
/// <c>SessionScored</c>. Đây là khoá để Campaign khớp điểm sàn read-time THEO ID (ổn định qua PUT),
/// không phải theo tên. null (bản Campaign cũ chưa gửi) ⇒ snapshot mang null ⇒ Campaign lùi về khớp tên.
/// </summary>
public class CriterionSourceIdRnk1B4Tests
{
    // ── Materialize: criterionId payload → rubric_criteria.source_criterion_id ────────────────────

    [Fact]
    public async Task Materialize_GhiCriterionId_XuongSourceCriterionId()
    {
        using var t = new TestDb();
        var reservation = new Mock<ICreditReservationClient>();
        reservation.Setup(r => r.ReserveAsync(
                It.IsAny<string>(), It.IsAny<Guid>(), It.IsAny<Guid>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(new CreditReservationResult(Guid.NewGuid(), 1));

        var svc = new PracticeService(
            t.Db, new Mock<IStorageService>().Object, new Mock<IAiServiceQuestionGenerator>().Object,
            new Mock<ISessionScoringNotifier>().Object, reservation.Object,
            NullLogger<PracticeService>.Instance,
            Options.Create(new AdaptiveOptions { Enabled = false }));

        var campaignCritId = Guid.NewGuid();
        var req = new CreateCampaignSessionRequest(
            Guid.NewGuid(), Guid.NewGuid(), JobCategory.BE,
            new[] { "Q1" },
            new[] { new CampaignCriterionInput("Communication", null, 1.0m, 5, CriterionId: campaignCritId) });

        await svc.CreateCampaignSessionAsync(Guid.NewGuid(), req);

        using var check = t.NewContext();
        var materialized = await check.RubricCriteria.AsNoTracking()
            .SingleAsync(c => c.CampaignId != null && c.Name == "Communication");
        Assert.Equal(campaignCritId, materialized.SourceCriterionId);
    }

    [Fact]
    public async Task Materialize_KhongCriterionId_SourceCriterionIdNull()
    {
        using var t = new TestDb();
        var reservation = new Mock<ICreditReservationClient>();
        reservation.Setup(r => r.ReserveAsync(
                It.IsAny<string>(), It.IsAny<Guid>(), It.IsAny<Guid>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(new CreditReservationResult(Guid.NewGuid(), 1));

        var svc = new PracticeService(
            t.Db, new Mock<IStorageService>().Object, new Mock<IAiServiceQuestionGenerator>().Object,
            new Mock<ISessionScoringNotifier>().Object, reservation.Object,
            NullLogger<PracticeService>.Instance,
            Options.Create(new AdaptiveOptions { Enabled = false }));

        var req = new CreateCampaignSessionRequest(
            Guid.NewGuid(), Guid.NewGuid(), JobCategory.BE,
            new[] { "Q1" },
            new[] { new CampaignCriterionInput("Communication", null, 1.0m, 5) });   // bản Campaign cũ

        await svc.CreateCampaignSessionAsync(Guid.NewGuid(), req);

        using var check = t.NewContext();
        var materialized = await check.RubricCriteria.AsNoTracking()
            .SingleAsync(c => c.CampaignId != null && c.Name == "Communication");
        Assert.Null(materialized.SourceCriterionId);
    }

    // ── Chấm: source_criterion_id → CriterionInputSnapshot.CriterionId của event ─────────────────

    private static (TestDb T, Guid SessionId) SeedScored(Guid? sourceCriterionId)
    {
        var t = new TestDb();
        var campaignId = Guid.NewGuid();
        var c = TestDb.Criterion(JobCategory.BE, version: 1, campaignId: campaignId, name: "Communication");
        c.SourceCriterionId = sourceCriterionId;
        t.Db.RubricCriteria.Add(c);

        var session = TestDb.Session(Guid.NewGuid(), SessionStatus.Scored, campaignId: campaignId);
        session.CampaignRubricVersion = 1;
        t.Db.Add(session);

        var q = TestDb.Question(session.Id, 1);
        q.Kind = QuestionKind.Seed;
        t.Db.Add(q);
        var a = TestDb.Answer(session.Id, q.Id, AnswerStatus.Scored, DateTime.UtcNow, DateTime.UtcNow);
        t.Db.Add(a);
        t.Db.AnswerScores.Add(new AnswerScore
        {
            AnswerId = a.Id, CriterionId = c.Id, Score = 4m, RubricVersion = 1
        });
        t.Db.SaveChanges();
        return (t, session.Id);
    }

    [Fact]
    public async Task Snapshot_MangCriterionId_TuSourceCriterionId()
    {
        var sourceId = Guid.NewGuid();
        var (t, s) = SeedScored(sourceId);
        using var _ = t;

        await TestDb.Notifier(t.Db).EnqueueSessionScoredAsync(s);
        await t.Db.SaveChangesAsync();
        var evt = TestDb.ScoredOutbox(t.NewContext(), s)!;

        var crit = Assert.Single(evt.ScoringInputs!.Criteria);
        Assert.Equal(sourceId, crit.CriterionId);
    }

    [Fact]
    public async Task Snapshot_SourceCriterionIdNull_CriterionIdNull()
    {
        var (t, s) = SeedScored(sourceCriterionId: null);
        using var _ = t;

        await TestDb.Notifier(t.Db).EnqueueSessionScoredAsync(s);
        await t.Db.SaveChangesAsync();
        var evt = TestDb.ScoredOutbox(t.NewContext(), s)!;

        Assert.Null(Assert.Single(evt.ScoringInputs!.Criteria).CriterionId);
    }
}
