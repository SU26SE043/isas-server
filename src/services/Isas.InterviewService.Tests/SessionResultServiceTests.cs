using Isas.InterviewService.DTOs;
using Isas.InterviewService.Entities;
using Isas.InterviewService.Enums;
using Isas.InterviewService.Services;
using Isas.InterviewService.Services.Interfaces;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging.Abstractions;
using Moq;

namespace Isas.InterviewService.Tests;

// BC9 — tổng kết điểm buổi luyện B2C (không AI, chỉ B2C).
public class SessionResultServiceTests
{
    // Thêm 1 answer_scores cho (answer, criterion). attempt_no=1, rubric_version=1.
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

    private static RubricCriterion Crit(JobCategory cat, string name, int maxScore, decimal weight)
        => new()
        {
            Id = Guid.NewGuid(),
            Name = name,
            Description = name,
            Weight = weight,
            MaxScore = maxScore,
            IsActive = true,
            JobCategory = cat,
            CampaignId = null,
            Version = 1
        };

    // ── (a) B2C Scored → overall_score set + rows đúng số tiêu chí ─────────────────────
    // Đồng thời chứng minh INT-10: điểm tổng = TRUNG BÌNH CỘNG pct (equal weight), KHÔNG dùng weight.
    [Fact]
    public async Task Compute_B2C_SetsOverallScore_EqualWeightAverage_AndWritesRowPerCriterion()
    {
        using var t = new TestDb();
        var candidate = Guid.NewGuid();
        var session = TestDb.Session(candidate, SessionStatus.Scored, JobCategory.BE);
        var q = TestDb.Question(session.Id);
        // 2 tiêu chí thang + weight KHÁC nhau — để phân biệt equal-weight vs weighted.
        var clarity = Crit(JobCategory.BE, "Clarity", maxScore: 5, weight: 0.4m);
        var depth = Crit(JobCategory.BE, "Depth", maxScore: 10, weight: 0.6m);
        var answer = TestDb.Answer(session.Id, q.Id, AnswerStatus.Scored, DateTime.UtcNow, DateTime.UtcNow);
        t.Db.AddRange(session, q, clarity, depth, answer);
        // Clarity 4/5 = 80% ; Depth 4/10 = 40%.
        t.Db.AddRange(Score(answer.Id, clarity.Id, 4m), Score(answer.Id, depth.Id, 4m));
        await t.Db.SaveChangesAsync();

        await BuildService(t).ComputeAndStoreAsync(session.Id);

        var s = await t.Db.PracticeSessions.AsNoTracking().FirstAsync(x => x.Id == session.Id);
        // Equal-weight = (80+40)/2 = 60. Weighted (B2B) sẽ là 80*0.4+40*0.6 = 56 → 60 chứng minh equal-weight.
        Assert.Equal(60m, s.OverallScore);
        Assert.Equal(1, s.AnsweredCount);

        var rows = await t.Db.SessionCriterionScores.AsNoTracking()
            .Where(x => x.SessionId == session.Id).ToListAsync();
        Assert.Equal(2, rows.Count);   // 1 row / tiêu chí

        var clarityRow = rows.Single(r => r.CriterionId == clarity.Id);
        Assert.Equal(4m, clarityRow.AverageScore);
        Assert.Equal(5, clarityRow.MaxScore);
        Assert.Equal(80m, clarityRow.Percentage);
        Assert.Equal(0.4m, clarityRow.Weight);
        Assert.Equal("Clarity", clarityRow.CriterionName);

        var depthRow = rows.Single(r => r.CriterionId == depth.Id);
        Assert.Equal(40m, depthRow.Percentage);
    }

    // Trung bình điểm mỗi tiêu chí qua NHIỀU câu đã chấm (BC9 §Công thức bước 1).
    [Fact]
    public async Task Compute_B2C_AveragesCriterionScoreAcrossAnsweredQuestions()
    {
        using var t = new TestDb();
        var candidate = Guid.NewGuid();
        var session = TestDb.Session(candidate, SessionStatus.Scored, JobCategory.BE);
        var q1 = TestDb.Question(session.Id, 1);
        var q2 = TestDb.Question(session.Id, 2);
        var clarity = Crit(JobCategory.BE, "Clarity", maxScore: 5, weight: 1.0m);
        var a1 = TestDb.Answer(session.Id, q1.Id, AnswerStatus.Scored, DateTime.UtcNow, DateTime.UtcNow);
        var a2 = TestDb.Answer(session.Id, q2.Id, AnswerStatus.Scored, DateTime.UtcNow, DateTime.UtcNow);
        t.Db.AddRange(session, q1, q2, clarity, a1, a2);
        // câu 1: 4/5 ; câu 2: 2/5 → TB = 3/5 = 60%.
        t.Db.AddRange(Score(a1.Id, clarity.Id, 4m), Score(a2.Id, clarity.Id, 2m));
        await t.Db.SaveChangesAsync();

        await BuildService(t).ComputeAndStoreAsync(session.Id);

        var s = await t.Db.PracticeSessions.AsNoTracking().FirstAsync(x => x.Id == session.Id);
        Assert.Equal(60m, s.OverallScore);
        Assert.Equal(2, s.AnsweredCount);   // 2 câu đã chấm

        var row = await t.Db.SessionCriterionScores.AsNoTracking().SingleAsync(x => x.SessionId == session.Id);
        Assert.Equal(3m, row.AverageScore);
        Assert.Equal(60m, row.Percentage);
    }

    // ── (c) needsImprovement lọc đúng tiêu chí dưới ngưỡng (mặc định 50%) ─────────────
    [Fact]
    public async Task Compute_B2C_FlagsNeedsImprovement_ForCriteriaBelowThreshold()
    {
        using var t = new TestDb();
        var candidate = Guid.NewGuid();
        var session = TestDb.Session(candidate, SessionStatus.Scored, JobCategory.BE);
        var q = TestDb.Question(session.Id);
        var strong = Crit(JobCategory.BE, "Strong", maxScore: 5, weight: 0.5m);   // 4/5 = 80% ≥ 50 → false
        var weak = Crit(JobCategory.BE, "Weak", maxScore: 5, weight: 0.5m);        // 2/5 = 40% < 50 → true
        var answer = TestDb.Answer(session.Id, q.Id, AnswerStatus.Scored, DateTime.UtcNow, DateTime.UtcNow);
        t.Db.AddRange(session, q, strong, weak, answer);
        t.Db.AddRange(Score(answer.Id, strong.Id, 4m), Score(answer.Id, weak.Id, 2m));
        await t.Db.SaveChangesAsync();

        await BuildService(t).ComputeAndStoreAsync(session.Id);

        var rows = await t.Db.SessionCriterionScores.AsNoTracking()
            .Where(x => x.SessionId == session.Id).ToListAsync();
        Assert.False(rows.Single(r => r.CriterionId == strong.Id).NeedsImprovement);
        Assert.True(rows.Single(r => r.CriterionId == weak.Id).NeedsImprovement);
    }

    // Đúng ranh giới ngưỡng: percentage == ngưỡng KHÔNG bị coi là cần cải thiện (< strict).
    [Fact]
    public async Task Compute_B2C_AtExactThreshold_IsNotNeedsImprovement()
    {
        using var t = new TestDb();
        var candidate = Guid.NewGuid();
        var session = TestDb.Session(candidate, SessionStatus.Scored, JobCategory.BE);
        var q = TestDb.Question(session.Id);
        var crit = Crit(JobCategory.BE, "OnThreshold", maxScore: 10, weight: 1.0m);   // 5/10 = 50%
        var answer = TestDb.Answer(session.Id, q.Id, AnswerStatus.Scored, DateTime.UtcNow, DateTime.UtcNow);
        t.Db.AddRange(session, q, crit, answer);
        t.Db.Add(Score(answer.Id, crit.Id, 5m));
        await t.Db.SaveChangesAsync();

        await BuildService(t).ComputeAndStoreAsync(session.Id);

        var row = await t.Db.SessionCriterionScores.AsNoTracking().SingleAsync(x => x.SessionId == session.Id);
        Assert.Equal(50m, row.Percentage);
        Assert.False(row.NeedsImprovement);   // 50 < 50 = false
    }

    // ── (d) CHỈ B2C — session B2B (campaign_id có) KHÔNG tính/ghi ─────────────────────
    [Fact]
    public async Task Compute_B2B_DoesNothing_NoRows_NoOverallScore()
    {
        using var t = new TestDb();
        var candidate = Guid.NewGuid();
        var campaignId = Guid.NewGuid();
        var session = TestDb.Session(candidate, SessionStatus.Scored, JobCategory.BE, campaignId: campaignId);
        var q = TestDb.Question(session.Id);
        var campaignCrit = TestDb.Criterion(JobCategory.BE, campaignId: campaignId, name: "Campaign-Crit");
        var answer = TestDb.Answer(session.Id, q.Id, AnswerStatus.Scored, DateTime.UtcNow, DateTime.UtcNow);
        t.Db.AddRange(session, q, campaignCrit, answer);
        t.Db.Add(Score(answer.Id, campaignCrit.Id, 4m));
        await t.Db.SaveChangesAsync();

        await BuildService(t).ComputeAndStoreAsync(session.Id);

        var s = await t.Db.PracticeSessions.AsNoTracking().FirstAsync(x => x.Id == session.Id);
        Assert.Null(s.OverallScore);     // B2B không tính
        Assert.Null(s.AnsweredCount);
        Assert.Empty(await t.Db.SessionCriterionScores.AsNoTracking().Where(x => x.SessionId == session.Id).ToListAsync());
    }

    // B2C chỉ dùng rubric nghề campaign_id IS NULL — tiêu chí campaign cùng nghề bị loại (E1).
    [Fact]
    public async Task Compute_B2C_ExcludesSameJobCategoryCampaignCriteria()
    {
        using var t = new TestDb();
        var candidate = Guid.NewGuid();
        var session = TestDb.Session(candidate, SessionStatus.Scored, JobCategory.BE);   // B2C
        var q = TestDb.Question(session.Id);
        var b2cCrit = Crit(JobCategory.BE, "B2C-Crit", maxScore: 5, weight: 1.0m);
        var campaignCrit = TestDb.Criterion(JobCategory.BE, campaignId: Guid.NewGuid(), name: "Campaign-Crit");
        var answer = TestDb.Answer(session.Id, q.Id, AnswerStatus.Scored, DateTime.UtcNow, DateTime.UtcNow);
        t.Db.AddRange(session, q, b2cCrit, campaignCrit, answer);
        t.Db.Add(Score(answer.Id, b2cCrit.Id, 4m));
        await t.Db.SaveChangesAsync();

        await BuildService(t).ComputeAndStoreAsync(session.Id);

        var rows = await t.Db.SessionCriterionScores.AsNoTracking()
            .Where(x => x.SessionId == session.Id).ToListAsync();
        Assert.Single(rows);                       // chỉ tiêu chí B2C, không có campaign
        Assert.Equal(b2cCrit.Id, rows[0].CriterionId);
    }

    // Idempotent: tính lại cùng session → xoá breakdown cũ, KHÔNG nhân đôi row.
    [Fact]
    public async Task Compute_CalledTwice_IsIdempotent_NoDuplicateRows()
    {
        using var t = new TestDb();
        var candidate = Guid.NewGuid();
        var session = TestDb.Session(candidate, SessionStatus.Scored, JobCategory.BE);
        var q = TestDb.Question(session.Id);
        var crit = Crit(JobCategory.BE, "Clarity", maxScore: 5, weight: 1.0m);
        var answer = TestDb.Answer(session.Id, q.Id, AnswerStatus.Scored, DateTime.UtcNow, DateTime.UtcNow);
        t.Db.AddRange(session, q, crit, answer);
        t.Db.Add(Score(answer.Id, crit.Id, 4m));
        await t.Db.SaveChangesAsync();

        var svc = BuildService(t);
        await svc.ComputeAndStoreAsync(session.Id);
        await svc.ComputeAndStoreAsync(session.Id);   // tính lại

        var count = await t.Db.SessionCriterionScores.AsNoTracking().CountAsync(x => x.SessionId == session.Id);
        Assert.Equal(1, count);   // không nhân đôi
    }

    // Edge: answeredCount=0 (mọi câu Failed, không có answer_scores) → overall=0, mọi tiêu chí cần cải thiện.
    [Fact]
    public async Task Compute_B2C_NoScoredAnswers_OverallZero_AllNeedImprovement()
    {
        using var t = new TestDb();
        var candidate = Guid.NewGuid();
        var session = TestDb.Session(candidate, SessionStatus.Scored, JobCategory.BE);
        var q = TestDb.Question(session.Id);
        var c1 = Crit(JobCategory.BE, "C1", maxScore: 5, weight: 0.5m);
        var c2 = Crit(JobCategory.BE, "C2", maxScore: 5, weight: 0.5m);
        // Answer Failed → KHÔNG có answer_scores.
        var answer = TestDb.Answer(session.Id, q.Id, AnswerStatus.Failed, DateTime.UtcNow, DateTime.UtcNow);
        t.Db.AddRange(session, q, c1, c2, answer);
        await t.Db.SaveChangesAsync();

        await BuildService(t).ComputeAndStoreAsync(session.Id);

        var s = await t.Db.PracticeSessions.AsNoTracking().FirstAsync(x => x.Id == session.Id);
        Assert.Equal(0m, s.OverallScore);
        Assert.Equal(0, s.AnsweredCount);

        var rows = await t.Db.SessionCriterionScores.AsNoTracking()
            .Where(x => x.SessionId == session.Id).ToListAsync();
        Assert.Equal(2, rows.Count);
        Assert.All(rows, r => Assert.Equal(0m, r.Percentage));
        Assert.All(rows, r => Assert.True(r.NeedsImprovement));   // tất cả tiêu chí
    }

    // ── (b) GET /sessions/{id} trả result đúng shape (đọc từ DB, không tính lại) ──────
    [Fact]
    public async Task GetSession_B2CScored_ReturnsResult_WithCriteriaAndNeedsImprovement()
    {
        using var t = new TestDb();
        var candidate = Guid.NewGuid();
        var session = TestDb.Session(candidate, SessionStatus.Scored, JobCategory.BE);
        var q = TestDb.Question(session.Id);
        var strong = Crit(JobCategory.BE, "Strong", maxScore: 5, weight: 0.5m);   // 80%
        var weak = Crit(JobCategory.BE, "Weak", maxScore: 5, weight: 0.5m);        // 40% → needsImprovement
        var answer = TestDb.Answer(session.Id, q.Id, AnswerStatus.Scored, DateTime.UtcNow, DateTime.UtcNow);
        t.Db.AddRange(session, q, strong, weak, answer);
        t.Db.AddRange(Score(answer.Id, strong.Id, 4m), Score(answer.Id, weak.Id, 2m));
        await t.Db.SaveChangesAsync();

        await BuildService(t).ComputeAndStoreAsync(session.Id);

        var resp = await BuildPractice(t).GetSessionAsync(candidate, session.Id);
        Assert.NotNull(resp);
        Assert.NotNull(resp!.Result);
        Assert.Equal(60m, resp.Result!.OverallScore);      // (80+40)/2
        Assert.Equal(1, resp.Result.AnsweredCount);
        Assert.Equal(1, resp.Result.TotalQuestions);
        Assert.Equal(2, resp.Result.CriteriaScores.Count);
        // needsImprovement chỉ chứa tiêu chí yếu.
        Assert.Equal(new[] { weak.Id }, resp.Result.NeedsImprovement);
        Assert.Null(resp.Result.OverallComment);           // BC10 chưa build
    }

    // GET buổi CHƯA Scored → result = null (không dựng tổng kết).
    [Fact]
    public async Task GetSession_NotScored_ResultIsNull()
    {
        using var t = new TestDb();
        var candidate = Guid.NewGuid();
        var session = TestDb.Session(candidate, SessionStatus.InProgress, JobCategory.BE);
        var q = TestDb.Question(session.Id);
        t.Db.AddRange(session, q);
        await t.Db.SaveChangesAsync();

        var resp = await BuildPractice(t).GetSessionAsync(candidate, session.Id);
        Assert.NotNull(resp);
        Assert.Null(resp!.Result);
    }

    // GET session B2B đã Scored → result = null (BC9 không áp B2B).
    [Fact]
    public async Task GetSession_B2BScored_ResultIsNull()
    {
        using var t = new TestDb();
        var candidate = Guid.NewGuid();
        var campaignId = Guid.NewGuid();
        var session = TestDb.Session(candidate, SessionStatus.Scored, JobCategory.BE, campaignId: campaignId);
        var q = TestDb.Question(session.Id);
        t.Db.AddRange(session, q);
        await t.Db.SaveChangesAsync();

        var resp = await BuildPractice(t).GetSessionAsync(candidate, session.Id);
        Assert.NotNull(resp);
        Assert.Null(resp!.Result);
    }

    // History trả overallScore của buổi đã Scored.
    [Fact]
    public async Task GetHistory_IncludesOverallScore()
    {
        using var t = new TestDb();
        var candidate = Guid.NewGuid();
        var session = TestDb.Session(candidate, SessionStatus.Scored, JobCategory.BE);
        var q = TestDb.Question(session.Id);
        var crit = Crit(JobCategory.BE, "Clarity", maxScore: 5, weight: 1.0m);
        var answer = TestDb.Answer(session.Id, q.Id, AnswerStatus.Scored, DateTime.UtcNow, DateTime.UtcNow);
        t.Db.AddRange(session, q, crit, answer);
        t.Db.Add(Score(answer.Id, crit.Id, 4m));
        await t.Db.SaveChangesAsync();

        await BuildService(t).ComputeAndStoreAsync(session.Id);

        var history = await BuildPractice(t).GetHistoryAsync(candidate);
        var item = Assert.Single(history);
        Assert.Equal(80m, item.OverallScore);   // 4/5
    }

    // ── Wired: B2C session đóng Scored qua AnswerService (real notifier) → BC9 ghi kết quả ──
    [Fact]
    public async Task SaveResult_B2CSession_WhenScored_PersistsSessionResult()
    {
        using var t = new TestDb();
        var candidate = Guid.NewGuid();
        var session = TestDb.Session(candidate, SessionStatus.Scoring, JobCategory.BE);   // B2C
        var q = TestDb.Question(session.Id);
        var crit = Crit(JobCategory.BE, "Clarity", maxScore: 5, weight: 1.0m);
        var answer = TestDb.Answer(session.Id, q.Id, AnswerStatus.Scoring, DateTime.UtcNow, DateTime.UtcNow);
        t.Db.AddRange(session, q, crit, answer);
        await t.Db.SaveChangesAsync();

        // Notifier THẬT + result service THẬT (chỉ mock transport event).
        var eventPublisher = new Mock<ISessionEventPublisher>();
        eventPublisher
            .Setup(p => p.PublishSessionScoredAsync(It.IsAny<SessionScoredEvent>(), It.IsAny<CancellationToken>()))
            .Returns(Task.CompletedTask);
        var notifier = new SessionScoringNotifier(
            t.Db, eventPublisher.Object, TestDb.ResultService(t.Db), TestDb.Summarizer(),
            NullLogger<SessionScoringNotifier>.Instance);
        var svc = new AnswerService(
            t.Db, new Mock<IStorageService>().Object, new Mock<IScoringJobPublisher>().Object,
            notifier, NullLogger<AnswerService>.Instance);

        await svc.SaveResultAsync(answer.Id, new AnswerScoreCallbackRequest
        {
            Transcript = "trả lời",
            RubricVersion = 1,
            Scores = { new ScoreItemDto { CriterionId = crit.Id, Score = 4m, Reasoning = "ok" } }
        });

        var s = await t.Db.PracticeSessions.AsNoTracking().FirstAsync(x => x.Id == session.Id);
        Assert.Equal(SessionStatus.Scored, s.Status);
        Assert.Equal(80m, s.OverallScore);   // 4/5 = 80%
        Assert.Equal(1, s.AnsweredCount);
        Assert.Single(await t.Db.SessionCriterionScores.AsNoTracking().Where(x => x.SessionId == session.Id).ToListAsync());
    }

    // ── helpers ───────────────────────────────────────────────────────────
    private static SessionResultService BuildService(TestDb t) => TestDb.ResultService(t.Db);

    private static PracticeService BuildPractice(TestDb t)
    {
        var notifier = new Mock<ISessionScoringNotifier>();
        notifier
            .Setup(n => n.NotifySessionScoredAsync(It.IsAny<Guid>(), It.IsAny<CancellationToken>()))
            .Returns(Task.CompletedTask);
        return new PracticeService(
            t.Db, new Mock<IStorageService>().Object,
            new Mock<IAiServiceQuestionGenerator>().Object, notifier.Object,
            new Mock<ICreditReservationClient>().Object,   // BC2: không dùng ở nhánh B2B
            new Mock<ISessionEventPublisher>().Object,     // BK12: không dùng ở nhánh B2B
            NullLogger<PracticeService>.Instance);
    }
}
