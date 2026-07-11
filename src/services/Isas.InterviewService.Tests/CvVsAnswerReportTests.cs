using Isas.InterviewService.ApplicationDbContext;
using Isas.InterviewService.DTOs;
using Isas.InterviewService.Entities;
using Isas.InterviewService.Enums;
using Isas.InterviewService.Services;
using Isas.InterviewService.Services.Interfaces;
using Microsoft.Extensions.Logging.Abstractions;
using Moq;

namespace Isas.InterviewService.Tests;

// BC8 — báo cáo "CV vs câu trả lời" cho buổi luyện B2C đã Scored.
// Định nghĩa (deterministic, KHÔNG AI): gap = tiêu chí VỪA yếu (needs_improvement, BC9)
// VỪA được CV thể hiện mạnh (token tên tiêu chí khớp strength/skill từ cv_analyses, BC7).
public class CvVsAnswerReportTests
{
    // ── Builder thuần (định nghĩa "mạnh/yếu") ─────────────────────────────────────
    private static SessionCriterionScore CritScore(string name, decimal pct, bool needsImprovement, int maxScore = 5)
        => new()
        {
            Id = Guid.NewGuid(),
            SessionId = Guid.NewGuid(),
            CriterionId = Guid.NewGuid(),
            CriterionName = name,
            AverageScore = Math.Round(pct / 100m * maxScore, 2),
            MaxScore = maxScore,
            Percentage = pct,
            Weight = 1m,
            NeedsImprovement = needsImprovement,
            CreatedAt = DateTime.UtcNow
        };

    // Không có CV đã phân tích (cvStrengths rỗng) → báo cáo ABSENT (null), KHÔNG lỗi.
    [Fact]
    public void Build_NoCvStrengths_ReturnsNull()
    {
        var report = CvVsAnswerReportBuilder.Build(
            Array.Empty<string>(),
            new[] { CritScore("Microservice Design", 40m, needsImprovement: true) });

        Assert.Null(report);
    }

    // Cốt lõi BC8: chỉ liệt kê tiêu chí VỪA yếu VỪA được CV thể hiện mạnh (token khớp), kèm bằng chứng CV.
    [Fact]
    public void Build_ListsOnlyWeakCriteriaCorroboratedByCvStrength()
    {
        var strengths = new[] { "Microservice architecture", "SQL databases", "Docker and Kubernetes" };
        var scores = new[]
        {
            CritScore("Microservice Design", 40m, needsImprovement: true),  // yếu + CV mạnh → gap
            CritScore("SQL Optimization",   30m, needsImprovement: true),   // yếu + CV mạnh → gap
            CritScore("Communication",      20m, needsImprovement: true),   // yếu nhưng CV KHÔNG nhắc → bỏ
            CritScore("System Design",      90m, needsImprovement: false),  // CV không nhắc + không yếu → bỏ
        };

        var report = CvVsAnswerReportBuilder.Build(strengths, scores)!;

        Assert.Equal(strengths, report.CvStrengths);
        Assert.Equal(2, report.Gaps.Count);

        var micro = report.Gaps.Single(g => g.CriterionName == "Microservice Design");
        Assert.Equal(40m, micro.Percentage);
        Assert.Equal(new[] { "Microservice architecture" }, micro.CvEvidence);   // đúng strength khớp

        var sql = report.Gaps.Single(g => g.CriterionName == "SQL Optimization");
        Assert.Equal(new[] { "SQL databases" }, sql.CvEvidence);

        Assert.DoesNotContain(report.Gaps, g => g.CriterionName == "Communication");
    }

    // Tiêu chí được CV nhắc mạnh NHƯNG answer KHÔNG yếu → KHÔNG phải gap (cần CẢ HAI điều kiện).
    [Fact]
    public void Build_ExcludesStrongCriterion_EvenIfCvMentionsIt()
    {
        var report = CvVsAnswerReportBuilder.Build(
            new[] { "Microservice architecture" },
            new[] { CritScore("Microservice Design", 85m, needsImprovement: false) })!;

        Assert.Empty(report.Gaps);
        Assert.Single(report.CvStrengths);   // báo cáo vẫn tồn tại (có strengths), chỉ không có gap
    }

    // Có CV nhưng KHÔNG tiêu chí nào CV nhắc tới → gaps rỗng, không lỗi.
    [Fact]
    public void Build_NoTokenOverlap_ReturnsEmptyGaps()
    {
        var report = CvVsAnswerReportBuilder.Build(
            new[] { "Public speaking", "Teamwork" },
            new[] { CritScore("Microservice Design", 40m, needsImprovement: true) })!;

        Assert.Empty(report.Gaps);
    }

    // ── Wiring qua GET /sessions/{id} (đọc DB thật, SQLite) ───────────────────────
    private static PracticeService BuildPractice(TestDb t)
    {
        var notifier = new Mock<ISessionScoringNotifier>();
        notifier.Setup(n => n.NotifySessionScoredAsync(It.IsAny<Guid>(), It.IsAny<CancellationToken>()))
            .Returns(Task.CompletedTask);
        return new PracticeService(
            t.Db, new Mock<IStorageService>().Object,
            new Mock<IAiServiceQuestionGenerator>().Object, notifier.Object,
            new Mock<ICreditReservationClient>().Object,
            new Mock<ISessionEventPublisher>().Object,   // BK12
            NullLogger<PracticeService>.Instance);
    }

    // Seed 1 rubric_criteria (thoả FK criterion_id) + 1 session_criterion_scores tương ứng (BC9 breakdown).
    private static void SeedScore(TestDb t, Guid sessionId, string name, decimal pct, bool needsImprovement)
    {
        var crit = TestDb.Criterion(JobCategory.BE, name: name);
        t.Db.Add(crit);
        t.Db.Add(new SessionCriterionScore
        {
            Id = Guid.NewGuid(),
            SessionId = sessionId,
            CriterionId = crit.Id,
            CriterionName = name,
            AverageScore = Math.Round(pct / 100m * 5m, 2),
            MaxScore = 5,
            Percentage = pct,
            Weight = 1m,
            NeedsImprovement = needsImprovement,
            CreatedAt = DateTime.UtcNow
        });
    }

    // PracticeSession.CvId có FK → file_records (Restrict) → phải seed file khi buổi gắn CvId.
    private static FileRecord CvFile(Guid cvId, Guid ownerId)
        => new()
        {
            Id = cvId,
            UserId = ownerId,
            FileType = "cv",
            OriginalName = "cv.pdf",
            StoragePath = $"cv/{cvId}.pdf",
            StorageBucket = "isas-files",
            MimeType = "application/pdf",
            FileSize = 1024,
            ParsedText = "cv text",
            ParseStatus = "done",
            CreatedAt = DateTime.UtcNow,
            UpdatedAt = DateTime.UtcNow
        };

    private static CvAnalysis CvAnalysisRow(Guid candidateId, Guid cvId, params string[] strengths)
        => new()
        {
            Id = Guid.NewGuid(),
            CandidateId = candidateId,
            CvId = cvId,
            JobCategory = JobCategory.BE,
            Summary = "backend",
            Strengths = strengths.ToList(),
            Weaknesses = [],
            Suggestions = [],
            CreatedAt = DateTime.UtcNow
        };

    // (a) B2C Scored CÓ CV → Result.CvVsAnswer liệt kê đúng "CV mạnh nhưng trả lời yếu".
    [Fact]
    public async Task GetSession_B2CScored_WithCv_ReturnsCvVsAnswerReport()
    {
        using var t = new TestDb();
        var candidate = Guid.NewGuid();
        var cvId = Guid.NewGuid();
        var session = TestDb.Session(candidate, SessionStatus.Scored, JobCategory.BE);
        session.CvId = cvId;
        session.OverallScore = 40m;
        session.AnsweredCount = 1;
        var q = TestDb.Question(session.Id);

        t.Db.AddRange(session, q, CvFile(cvId, candidate));
        SeedScore(t, session.Id, "Microservice Design", 40m, needsImprovement: true);   // yếu + CV mạnh → gap
        SeedScore(t, session.Id, "Communication",       20m, needsImprovement: true);   // yếu nhưng CV không nhắc → bỏ
        t.Db.Add(CvAnalysisRow(candidate, cvId, "Microservice architecture", "SQL databases"));
        await t.Db.SaveChangesAsync();

        var resp = await BuildPractice(t).GetSessionAsync(candidate, session.Id);

        Assert.NotNull(resp!.Result);
        var report = resp.Result!.CvVsAnswer;
        Assert.NotNull(report);
        Assert.Equal(new[] { "Microservice architecture", "SQL databases" }, report!.CvStrengths);
        var gap = Assert.Single(report.Gaps);
        Assert.Equal("Microservice Design", gap.CriterionName);
        Assert.Equal(40m, gap.Percentage);
        Assert.Equal(new[] { "Microservice architecture" }, gap.CvEvidence);
    }

    // Gộp matched skills (JD match) vào tín hiệu "CV mạnh".
    [Fact]
    public async Task GetSession_MergesJdMatchedSkillsIntoStrengths()
    {
        using var t = new TestDb();
        var candidate = Guid.NewGuid();
        var cvId = Guid.NewGuid();
        var session = TestDb.Session(candidate, SessionStatus.Scored, JobCategory.BE);
        session.CvId = cvId;
        session.OverallScore = 30m;
        session.AnsweredCount = 1;
        var q = TestDb.Question(session.Id);

        var cv = CvAnalysisRow(candidate, cvId, "Backend fundamentals");
        cv.JdMatch = new CvJdMatch(70, ["Kubernetes orchestration"], ["GraphQL"]);

        t.Db.AddRange(session, q, CvFile(cvId, candidate));
        SeedScore(t, session.Id, "Kubernetes Networking", 30m, needsImprovement: true);  // khớp matched skill
        t.Db.Add(cv);
        await t.Db.SaveChangesAsync();

        var resp = await BuildPractice(t).GetSessionAsync(candidate, session.Id);

        var report = resp!.Result!.CvVsAnswer!;
        Assert.Contains("Kubernetes orchestration", report.CvStrengths);   // matched skill được gộp
        var gap = Assert.Single(report.Gaps);
        Assert.Equal(new[] { "Kubernetes orchestration" }, gap.CvEvidence);
    }

    // (b) B2C Scored KHÔNG có CV (CvId null) → Result vẫn có (BC9) nhưng CvVsAnswer ABSENT, KHÔNG lỗi.
    [Fact]
    public async Task GetSession_B2CScored_NoCv_ResultPresent_CvVsAnswerNull()
    {
        using var t = new TestDb();
        var candidate = Guid.NewGuid();
        var session = TestDb.Session(candidate, SessionStatus.Scored, JobCategory.BE);   // CvId null
        session.OverallScore = 40m;
        session.AnsweredCount = 1;
        var q = TestDb.Question(session.Id);

        t.Db.AddRange(session, q);
        SeedScore(t, session.Id, "Microservice Design", 40m, needsImprovement: true);
        await t.Db.SaveChangesAsync();

        var resp = await BuildPractice(t).GetSessionAsync(candidate, session.Id);

        Assert.NotNull(resp!.Result);            // BC9 vẫn có
        Assert.Null(resp.Result!.CvVsAnswer);    // BC8 absent (không CV)
    }

    // Có CvId nhưng CHƯA chạy phân tích CV (BC7) → CvVsAnswer absent, không lỗi.
    [Fact]
    public async Task GetSession_CvIdButNoAnalysis_CvVsAnswerNull()
    {
        using var t = new TestDb();
        var candidate = Guid.NewGuid();
        var cvId = Guid.NewGuid();
        var session = TestDb.Session(candidate, SessionStatus.Scored, JobCategory.BE);
        session.CvId = cvId;                     // có CvId nhưng không có row cv_analyses
        session.OverallScore = 40m;
        session.AnsweredCount = 1;
        var q = TestDb.Question(session.Id);

        t.Db.AddRange(session, q, CvFile(cvId, candidate));
        SeedScore(t, session.Id, "Microservice Design", 40m, needsImprovement: true);
        await t.Db.SaveChangesAsync();

        var resp = await BuildPractice(t).GetSessionAsync(candidate, session.Id);

        Assert.NotNull(resp!.Result);
        Assert.Null(resp.Result!.CvVsAnswer);
    }

    // (c) B2B Scored → Result null (BC9 không áp) → CvVsAnswer không được dựng.
    [Fact]
    public async Task GetSession_B2BScored_ResultNull_NoReport()
    {
        using var t = new TestDb();
        var candidate = Guid.NewGuid();
        var cvId = Guid.NewGuid();
        var session = TestDb.Session(candidate, SessionStatus.Scored, JobCategory.BE, campaignId: Guid.NewGuid());
        session.CvId = cvId;
        var q = TestDb.Question(session.Id);

        t.Db.AddRange(session, q, CvFile(cvId, candidate));
        t.Db.Add(CvAnalysisRow(candidate, cvId, "Microservice architecture"));
        await t.Db.SaveChangesAsync();

        var resp = await BuildPractice(t).GetSessionAsync(candidate, session.Id);

        Assert.Null(resp!.Result);   // B2B → không tổng kết, không CV report
    }

    // (c) B2C CHƯA Scored → Result null → CvVsAnswer không được dựng.
    [Fact]
    public async Task GetSession_NotScored_ResultNull_NoReport()
    {
        using var t = new TestDb();
        var candidate = Guid.NewGuid();
        var cvId = Guid.NewGuid();
        var session = TestDb.Session(candidate, SessionStatus.InProgress, JobCategory.BE);
        session.CvId = cvId;
        var q = TestDb.Question(session.Id);

        t.Db.AddRange(session, q, CvFile(cvId, candidate));
        t.Db.Add(CvAnalysisRow(candidate, cvId, "Microservice architecture"));
        await t.Db.SaveChangesAsync();

        var resp = await BuildPractice(t).GetSessionAsync(candidate, session.Id);

        Assert.Null(resp!.Result);
    }
}
