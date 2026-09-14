using System.Text.Json;
using Isas.InterviewService.Controllers;
using Isas.InterviewService.DTOs;
using Isas.InterviewService.Entities;
using Isas.InterviewService.Enums;
using Isas.InterviewService.Models;
using Isas.InterviewService.Services;
using Isas.InterviewService.Services.Interfaces;
using Isas.Shared.Scoring;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using Moq;
using Xunit;

namespace Isas.InterviewService.Tests;

/// <summary>
/// RNK1 · HĐ-1 / HĐ-2 — luật "câu HR khai mà ứng viên bỏ trống tính 0 điểm" trên đường chấm LIVE.
///
/// <para>SessionScoringNotifier: buổi <c>skip_penalty = true</c> ⇒ điểm tổng =
/// <c>clamp(expr × seed_completeness, 0, 100)</c> qua <see cref="SkipPenaltyRule"/>; snapshot mang
/// <c>seedAnswered</c>/<c>seedTotal</c>/<c>skipPenalty</c>. Câu đào sâu (FollowUp/Clarify) KHÔNG
/// tính vào <c>seed_*</c>. B2C + campaign trước RNK1 (<c>skip_penalty = false</c>) không đổi điểm.</para>
/// </summary>
public class SkipPenaltySessionRnk1Tests
{
    // 1 tiêu chí weight 1.0 maxScore 5 ⇒ weighted_avg_pct = score/5*100 (score 4 ⇒ 80).
    private static (TestDb T, Guid SessionId) Seed(
        bool skipPenalty, string? expr = null,
        int seedTotal = 5, int seedAnswered = 3, int deepTotal = 0, int deepAnswered = 0,
        decimal score = 4m)
    {
        var t = new TestDb();
        var campaignId = Guid.NewGuid();
        var c = TestDb.Criterion(JobCategory.BE, version: 1, campaignId: campaignId, name: "Communication");
        t.Db.RubricCriteria.Add(c);

        var session = TestDb.Session(Guid.NewGuid(), SessionStatus.Scored, campaignId: campaignId);
        session.CampaignRubricVersion = 1;
        session.SkipPenalty = skipPenalty;
        session.CampaignPolicyExpression = expr;
        session.CampaignPolicyVersion = expr is null ? null : 7;
        session.CampaignPolicyEngineVersion = expr is null ? null : "1";
        t.Db.Add(session);

        var order = 1;
        var firstAnswered = AddQuestions(t, session.Id, QuestionKind.Seed, seedTotal, seedAnswered, ref order, score, c.Id);
        AddQuestions(t, session.Id, QuestionKind.FollowUp, deepTotal, deepAnswered, ref order, score, c.Id, firstAnswered);

        t.Db.SaveChanges();
        return (t, session.Id);
    }

    // Tạo `total` câu kind `kind`; `answered` câu đầu có ghi âm (AudioObjectKey != null) + 1 AnswerScore.
    private static bool AddQuestions(
        TestDb t, Guid sessionId, QuestionKind kind, int total, int answered,
        ref int order, decimal score, Guid criterionId, bool alreadyHasScore = false)
    {
        for (var i = 0; i < total; i++)
        {
            var q = TestDb.Question(sessionId, order++);
            q.Kind = kind;
            t.Db.Add(q);

            if (i >= answered) continue;

            var a = TestDb.Answer(sessionId, q.Id, AnswerStatus.Scored, DateTime.UtcNow, DateTime.UtcNow);
            t.Db.Add(a);
            t.Db.AnswerScores.Add(new AnswerScore
            {
                AnswerId = a.Id, CriterionId = criterionId, Score = score, RubricVersion = 1
            });
        }
        return answered > 0;
    }

    private static async Task<SessionScoredEvent> Score(TestDb t, Guid sessionId)
    {
        await TestDb.Notifier(t.Db).EnqueueSessionScoredAsync(sessionId);
        await t.Db.SaveChangesAsync();
        return TestDb.ScoredOutbox(t.NewContext(), sessionId)!;
    }

    // ── HĐ-2: skip_penalty = true ⇒ nhân seed_completeness (nhánh MẶC ĐỊNH, chưa ghim policy) ──────
    [Fact]
    public async Task SkipPenaltyTrue_KhongGhimPolicy_NhanSeedCompleteness()
    {
        // 3/5 câu gốc trả lời + 2 câu đào sâu (đều trả lời) ⇒ answered 5, total 7, seed 3/5.
        var (t, s) = Seed(skipPenalty: true, seedTotal: 5, seedAnswered: 3, deepTotal: 2, deepAnswered: 2);
        using var _ = t;

        var evt = await Score(t, s);

        Assert.False(evt.ScoreFallback);
        Assert.Equal(48m, evt.TotalScore);                 // 80 × 3/5
        Assert.Equal(3, evt.ScoringInputs!.SeedAnswered);
        Assert.Equal(5, evt.ScoringInputs.SeedTotal);
        Assert.True(evt.ScoringInputs.SkipPenalty);
        Assert.Equal(5, evt.ScoringInputs.Answered);        // 3 seed + 2 đào sâu
        Assert.Equal(7, evt.ScoringInputs.TotalQuestions);  // 5 seed + 2 đào sâu
    }

    // ── HĐ-2: skip_penalty = true + ĐÃ ghim policy ⇒ nhân SAU khi đánh giá biểu thức ──────────────
    [Fact]
    public async Task SkipPenaltyTrue_CoPolicy_NhanSauEvaluate()
    {
        var (t, s) = Seed(skipPenalty: true, expr: "weighted_avg_pct", seedTotal: 4, seedAnswered: 2);
        using var _ = t;

        var evt = await Score(t, s);

        Assert.False(evt.ScoreFallback);
        Assert.Equal(40m, evt.TotalScore);   // 80 × 2/4
    }

    // ── HĐ-2: skip_penalty = false ⇒ KHÔNG nhân (parity điểm trước RNK1) ─────────────────────────
    [Fact]
    public async Task SkipPenaltyFalse_KhongNhan()
    {
        var (t, s) = Seed(skipPenalty: false, seedTotal: 5, seedAnswered: 3, deepTotal: 2, deepAnswered: 2);
        using var _ = t;

        var evt = await Score(t, s);

        Assert.Equal(80m, evt.TotalScore);
        Assert.False(evt.ScoringInputs!.SkipPenalty);
        Assert.Equal(3, evt.ScoringInputs.SeedAnswered);   // vẫn ĐO seed_* (chỉ không áp luật)
        Assert.Equal(5, evt.ScoringInputs.SeedTotal);
    }

    // ── HĐ-1: câu đào sâu (FollowUp/Clarify) KHÔNG tính vào seed_* ⇒ trả lời hết câu GỐC ⇒ 0 giảm ─
    [Fact]
    public async Task CauDaoSau_KhongTinhVaoSeed_TraLoiHetCauGoc_KhongGiam()
    {
        // 2/2 câu gốc + 3 câu đào sâu (chỉ 1 trả lời) ⇒ seed 2/2 ⇒ seed_completeness = 1.
        var (t, s) = Seed(skipPenalty: true, seedTotal: 2, seedAnswered: 2, deepTotal: 3, deepAnswered: 1);
        using var _ = t;

        var evt = await Score(t, s);

        Assert.Equal(80m, evt.TotalScore);                  // 80 × 2/2
        Assert.Equal(2, evt.ScoringInputs!.SeedAnswered);
        Assert.Equal(2, evt.ScoringInputs.SeedTotal);
        Assert.Equal(3, evt.ScoringInputs.Answered);        // 2 seed + 1 đào sâu
        Assert.Equal(5, evt.ScoringInputs.TotalQuestions);  // 2 seed + 3 đào sâu
    }

    // ── B2C (campaign_id null, skip_penalty mặc định false) — KHÔNG regress ──────────────────────
    [Fact]
    public async Task B2C_KhongRegress()
    {
        var t = new TestDb();
        using var _ = t;
        var c = TestDb.Criterion(JobCategory.BE, version: 1, name: "Communication");
        t.Db.RubricCriteria.Add(c);
        var session = TestDb.Session(Guid.NewGuid(), SessionStatus.Scored);   // campaignId null
        t.Db.Add(session);
        var q = TestDb.Question(session.Id, 1);
        t.Db.Add(q);
        var a = TestDb.Answer(session.Id, q.Id, AnswerStatus.Scored, DateTime.UtcNow, DateTime.UtcNow);
        t.Db.Add(a);
        t.Db.AnswerScores.Add(new AnswerScore { AnswerId = a.Id, CriterionId = c.Id, Score = 4m, RubricVersion = 1 });
        t.Db.SaveChanges();

        var evt = await Score(t, session.Id);

        Assert.Equal(80m, evt.TotalScore);
        Assert.False(session.SkipPenalty);
        Assert.False(evt.ScoringInputs!.SkipPenalty);
    }

    // ── HĐ-2 dây: InternalSessionsController chuyển tiếp skipPenalty xuống service ────────────────
    [Fact]
    public async Task Controller_ForwardsSkipPenalty_ToService()
    {
        var svc = new Mock<IPracticeService>();
        CreateCampaignSessionRequest? captured = null;
        svc.Setup(s => s.GetOrCreateCampaignSessionAsync(
                It.IsAny<Guid>(), It.IsAny<CreateCampaignSessionRequest>(), It.IsAny<CancellationToken>()))
            .Callback<Guid, CreateCampaignSessionRequest, CancellationToken>((_, r, _) => captured = r)
            .ReturnsAsync(new PracticeSessionResponse(
                Guid.NewGuid(), "Ready", "BE", "vi", null, null, DateTime.UtcNow, null, []));

        var config = new ConfigurationBuilder()
            .AddInMemoryCollection(new Dictionary<string, string?> { ["Internal:Token"] = "tok" }).Build();
        var controller = new InternalSessionsController(
            svc.Object, config, NullLogger<InternalSessionsController>.Instance);

        var req = new CreateCampaignSessionInternalRequest(
            Guid.NewGuid(), Guid.NewGuid(), Guid.NewGuid(), "BE",
            new[] { "Q1" },
            new[] { new CampaignCriterionInput("Communication", null, 1.0m, 5) },
            SkipPenalty: true);

        await controller.CreateOrGetCampaignSession(req, "tok", CancellationToken.None);

        Assert.NotNull(captured);
        Assert.True(captured!.SkipPenalty);
    }

    // ── HĐ-2: PracticeService ghi request.SkipPenalty ?? false xuống practice_sessions.skip_penalty ─
    [Theory]
    [InlineData(true, true)]
    [InlineData(false, false)]
    [InlineData(null, false)]   // bản Campaign cũ chưa gửi
    public async Task CreateCampaignSession_GhimSkipPenalty(bool? sent, bool expected)
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
            new[] { new CampaignCriterionInput("Communication", null, 1.0m, 5) },
            SkipPenalty: sent);

        var res = await svc.CreateCampaignSessionAsync(Guid.NewGuid(), req);

        var stored = await t.NewContext().PracticeSessions.AsNoTracking().SingleAsync(s => s.Id == res.Id);
        Assert.Equal(expected, stored.SkipPenalty);
    }

    // ── HĐ-1 wire: khoá JSON camelCase "skipPenalty" (JsonSerializerDefaults.Web) ────────────────
    [Fact]
    public void InternalRequest_DeserializeKhoaCamelCase_SkipPenalty()
    {
        var opts = new JsonSerializerOptions(JsonSerializerDefaults.Web);

        var withFlag = JsonSerializer.Deserialize<CreateCampaignSessionInternalRequest>(
            """{"candidateId":"11111111-1111-1111-1111-111111111111","campaignId":"22222222-2222-2222-2222-222222222222","orgId":"33333333-3333-3333-3333-333333333333","jobCategory":"BE","questions":["Q1"],"criteria":[],"skipPenalty":true}""",
            opts)!;
        Assert.True(withFlag.SkipPenalty);

        // Bản Campaign cũ KHÔNG gửi khoá ⇒ null ⇒ session.skip_penalty = false.
        var without = JsonSerializer.Deserialize<CreateCampaignSessionInternalRequest>(
            """{"candidateId":"11111111-1111-1111-1111-111111111111","campaignId":"22222222-2222-2222-2222-222222222222","orgId":"33333333-3333-3333-3333-333333333333","jobCategory":"BE","questions":["Q1"],"criteria":[]}""",
            opts)!;
        Assert.Null(without.SkipPenalty);
    }
}
