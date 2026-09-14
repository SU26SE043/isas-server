using Isas.InterviewService.DTOs;
using Isas.InterviewService.Entities;
using Isas.InterviewService.Enums;
using Isas.InterviewService.Models;
using Isas.InterviewService.Services;
using Isas.InterviewService.Services.Interfaces;
using Isas.Shared.Scoring;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using Moq;
using Xunit;

namespace Isas.InterviewService.Tests;

/// <summary>
/// SCP1-B5 (Interview) — GHIM hợp đồng chấm điểm (chính sách biểu thức) vào practice_sessions lúc
/// tạo session; và BÓ BIẾN ĐẦU VÀO THÔ đi kèm event SessionScored (RAW per-criterion, KHÔNG scalar).
/// </summary>
public class ScoringPolicyPinB5Tests
{
    private static PracticeService Build(TestDb t)
    {
        var notifier = new Mock<ISessionScoringNotifier>();
        notifier.Setup(n => n.NotifySessionScoredAsync(It.IsAny<Guid>(), It.IsAny<CancellationToken>()))
            .Returns(Task.CompletedTask);
        var credits = new Mock<ICreditReservationClient>();
        credits.Setup(r => r.ReserveAsync(
                It.IsAny<string>(), It.IsAny<Guid>(), It.IsAny<Guid>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(new CreditReservationResult(Guid.NewGuid(), 1));
        return new PracticeService(t.Db, new Mock<IStorageService>().Object,
            new Mock<IAiServiceQuestionGenerator>().Object, notifier.Object, credits.Object,
            NullLogger<PracticeService>.Instance,
            capacityOptions: Options.Create(new CapacityOptions()));
    }

    private static CreateCampaignSessionRequest Request(
        Guid campaignId,
        int? policyVersion = null, string? policyExpr = null,
        int? policyPass = null, string? policyEngine = null)
        => new(campaignId, Guid.NewGuid(), JobCategory.BE,
            Questions: new[] { "Q1" },
            Criteria: new[] { new CampaignCriterionInput("Communication", null, 1.0m, 5) },
            CampaignPolicyVersion: policyVersion,
            CampaignPolicyExpression: policyExpr,
            CampaignPolicyPassScorePct: policyPass,
            CampaignPolicyEngineVersion: policyEngine);

    // ── (1) Ghim 4 cột chính sách lúc tạo session ─────────────────────────────────────────────
    [Fact]
    public async Task CreateCampaignSession_GhimHopDongCham_Vao4Cot()
    {
        using var t = new TestDb();
        var campaignId = Guid.NewGuid();

        var res = await Build(t).CreateCampaignSessionAsync(Guid.NewGuid(),
            Request(campaignId, policyVersion: 3, policyExpr: "weighted_avg_pct * completeness",
                policyPass: 55, policyEngine: "1"));

        var s = await t.NewContext().PracticeSessions.SingleAsync(x => x.Id == res.Id);
        Assert.Equal(3, s.CampaignPolicyVersion);
        Assert.Equal("weighted_avg_pct * completeness", s.CampaignPolicyExpression);
        Assert.Equal(55, s.CampaignPolicyPassScorePct);
        Assert.Equal("1", s.CampaignPolicyEngineVersion);
    }

    [Fact]
    public async Task CreateCampaignSession_KhongCoChinhSach_4CotDeNull()
    {
        using var t = new TestDb();
        var campaignId = Guid.NewGuid();

        var res = await Build(t).CreateCampaignSessionAsync(Guid.NewGuid(), Request(campaignId));

        var s = await t.NewContext().PracticeSessions.SingleAsync(x => x.Id == res.Id);
        Assert.Null(s.CampaignPolicyVersion);
        Assert.Null(s.CampaignPolicyExpression);
        Assert.Null(s.CampaignPolicyPassScorePct);
        Assert.Null(s.CampaignPolicyEngineVersion);
        // Không ảnh hưởng ghim rubric_version (CAMP-18) đang có.
        Assert.Equal(1, s.CampaignRubricVersion);
    }

    // ── (2) Bó biến THÔ trong event SessionScored ─────────────────────────────────────────────
    private static (TestDb T, Guid SessionId) SeedScoredSession(int score = 4)
    {
        var t = new TestDb();
        var campaignId = Guid.NewGuid();
        var c1 = TestDb.Criterion(JobCategory.BE, version: 1, campaignId: campaignId, name: "Communication");
        var c2 = TestDb.Criterion(JobCategory.BE, version: 1, campaignId: campaignId, name: "Technical depth");
        t.Db.RubricCriteria.AddRange(c1, c2);

        var session = TestDb.Session(Guid.NewGuid(), SessionStatus.Scored, campaignId: campaignId);
        session.CampaignRubricVersion = 1;
        t.Db.Add(session);
        var q = TestDb.Question(session.Id);
        t.Db.Add(q);
        var a = TestDb.Answer(session.Id, q.Id, AnswerStatus.Scored, DateTime.UtcNow, DateTime.UtcNow);
        t.Db.Add(a);
        foreach (var c in new[] { c1, c2 })
            t.Db.AnswerScores.Add(new AnswerScore { AnswerId = a.Id, CriterionId = c.Id, Score = score, RubricVersion = 1 });
        t.Db.SaveChanges();
        return (t, session.Id);
    }

    [Fact]
    public async Task ScoredEvent_MangBoBienThoPerCriterion()
    {
        var (t, sessionId) = SeedScoredSession(score: 4);   // 4/5 = 80%
        using var _ = t;

        await TestDb.Notifier(t.Db).EnqueueSessionScoredAsync(sessionId);
        await t.Db.SaveChangesAsync();

        var evt = TestDb.ScoredOutbox(t.NewContext(), sessionId)!;
        Assert.NotNull(evt.ScoringInputs);
        var bag = evt.ScoringInputs!;
        Assert.Equal(1, bag.Answered);
        Assert.Equal(1, bag.TotalQuestions);
        Assert.Equal(2, bag.Criteria.Count);
        Assert.All(bag.Criteria, c =>
        {
            Assert.Equal(80m, c.Pct);        // RAW per-criterion pct — không phải scalar tổng
            Assert.Equal(1.0m, c.Weight);
            Assert.Equal(5, c.MaxScore);
            Assert.False(string.IsNullOrWhiteSpace(c.Name));
        });
        Assert.Contains(bag.Criteria, c => c.Name == "Communication");
        Assert.Contains(bag.Criteria, c => c.Name == "Technical depth");
    }

    [Fact]
    public async Task BoBienTho_DungLaiDuoc_ScoringExpression_KhopTotalScore()
    {
        var (t, sessionId) = SeedScoredSession(score: 4);
        using var _ = t;

        await TestDb.Notifier(t.Db).EnqueueSessionScoredAsync(sessionId);
        await t.Db.SaveChangesAsync();
        var evt = TestDb.ScoredOutbox(t.NewContext(), sessionId)!;

        // B8 sẽ làm đúng thao tác này: dựng ScoringContext từ bó THÔ rồi chạy biểu thức.
        var ctx = ScoringContext.ForInterview(evt.ScoringInputs!.ToInterviewInputs());
        var r = ScoringExpression.Parse("weighted_avg_pct").Evaluate(ctx);

        Assert.True(r.Ok);
        Assert.Equal(evt.TotalScore, r.Value);   // 80 — tính LẠI từ THÔ, không lấy scalar lưu sẵn
    }

    [Fact]
    public async Task ScoredEvent_JsonPayload_KhongChua_Scalar_DaTinh()
    {
        var (t, sessionId) = SeedScoredSession();
        using var _ = t;

        await TestDb.Notifier(t.Db).EnqueueSessionScoredAsync(sessionId);
        await t.Db.SaveChangesAsync();

        var payload = t.NewContext().OutboxMessages.Single(m => m.Type == OutboxMessage.SessionScoredType).Payload;
        Assert.Contains("scoringInputs", payload, StringComparison.OrdinalIgnoreCase);
        Assert.Contains("criteria", payload, StringComparison.OrdinalIgnoreCase);
        // Cấm #3 — KHÔNG lưu scalar tổng đã tính (weighted_avg_pct / avg_pct / min_pct...).
        Assert.DoesNotContain("weighted_avg_pct", payload, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("weightedAvgPct", payload, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("avgPct", payload, StringComparison.OrdinalIgnoreCase);
    }
}
