using System.Security.Claims;
using System.Text.Json;
using Isas.InterviewService.Controllers;
using Isas.InterviewService.DTOs;
using Isas.InterviewService.Entities;
using Isas.InterviewService.Enums;
using Isas.InterviewService.Models;
using Isas.InterviewService.Services;
using Isas.InterviewService.Services.Interfaces;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using Moq;

namespace Isas.InterviewService.Tests;

// BC15 (D20) — hoàn tất milestone (improvement) / roadmap (final_report + AI comment) + GET /report.
public class RoadmapReportTests
{
    private static readonly JsonSerializerOptions Json = new(JsonSerializerDefaults.Web);

    // ── Builders ────────────────────────────────────────────────────────────
    // Session B2C Scored + breakdown session_criterion_scores (per tiêu chí %). Persist trước roadmap (FK).
    // criterion_id → rubric_criteria (FK Restrict) nên seed 1 RubricCriterion / tiêu chí trước.
    private static Guid AddScoredSession(TestDb t, Guid cand, params (string name, decimal pct)[] scores)
    {
        var session = TestDb.Session(cand, SessionStatus.Scored);
        t.Db.PracticeSessions.Add(session);

        // Câu hỏi + answer ĐÃ CHẤM đứng sau breakdown. Trước đây helper chỉ seed thẳng
        // session_criterion_scores mà KHÔNG có answer_scores nào — một trạng thái production không
        // dựng được (bảng này chỉ do SessionResultService ghi, và nó ghi từ answer_scores). Trạng thái
        // giả đó chỉ vô hại chừng nào BC9 còn "sửa" nó bằng cách ghi đè dòng 0.00; nay BC9 bỏ hẳn dòng
        // không có điểm nên test integration chạy notifier THẬT sẽ thấy breakdown rỗng.
        var question = TestDb.Question(session.Id);
        var answer = TestDb.Answer(
            session.Id, question.Id, AnswerStatus.Scored, DateTime.UtcNow, DateTime.UtcNow);
        t.Db.AddRange(question, answer);

        foreach (var (name, pct) in scores)
        {
            // DÙNG LẠI tiêu chí cùng tên nếu buổi trước đã seed — đúng như production: mọi buổi luyện
            // cùng (nghề, ngôn ngữ) chia sẻ MỘT bộ tiêu chí, id không đổi giữa các buổi. Tạo bản sao
            // mới mỗi buổi vừa dựng một trạng thái không tồn tại được (unique
            // `ux_rubric_criteria_b2c_default_version_name` chặn), vừa làm test yếu hơn ý định của nó:
            // báo cáo tiến bộ gom theo TÊN nên id trùng mới là ca thật.
            var criterion = t.Db.RubricCriteria.Local
                    .FirstOrDefault(c => c.Name == name && c.CandidateId == null
                                         && c.JobCategory == JobCategory.BE)
                ?? t.Db.RubricCriteria
                    .FirstOrDefault(c => c.Name == name && c.CandidateId == null
                                         && c.JobCategory == JobCategory.BE);
            if (criterion is null)
            {
                criterion = TestDb.Criterion(JobCategory.BE, name: name);   // MaxScore 5, Weight 1.0
                t.Db.RubricCriteria.Add(criterion);
            }
            // Điểm thô sinh ra ĐÚNG pct mong muốn khi BC9 tính lại: maxScore 5 ⇒ pct = score/5*100.
            t.Db.AnswerScores.Add(new AnswerScore
            {
                Id = Guid.NewGuid(),
                AnswerId = answer.Id,
                CriterionId = criterion.Id,
                Score = Math.Round(pct / 20m, 2),
                AttemptNo = 1,
                RubricVersion = 1,
                CreatedAt = DateTime.UtcNow
            });
            t.Db.SessionCriterionScores.Add(new SessionCriterionScore
            {
                Id = Guid.NewGuid(),
                SessionId = session.Id,
                CriterionId = criterion.Id,
                CriterionName = name,
                AverageScore = Math.Round(pct / 20m, 2),   // maxScore 5 ⇒ pct = avg/5*100
                MaxScore = 5,
                Percentage = pct,
                Weight = 1m,
                NeedsImprovement = pct < 50m,
                CreatedAt = DateTime.UtcNow
            });
        }
        t.Db.SaveChanges();
        return session.Id;
    }

    private static Roadmap NewRoadmap(Guid cand, Dictionary<string, decimal>? baseline)
        => new()
        {
            Id = Guid.NewGuid(),
            CandidateId = cand,
            JobCategory = JobCategory.BE,
            Level = RoadmapLevel.Junior,   // ngưỡng 60
            Baseline = baseline,
            Status = RoadmapStatus.Active,
            CreatedAt = DateTime.UtcNow
        };

    private static RoadmapMilestone AddMilestone(Roadmap r, int order, MilestoneStatus status, params string[] focus)
    {
        var m = new RoadmapMilestone
        {
            Id = Guid.NewGuid(),
            OrderNo = order,
            Title = $"M{order}",
            FocusCriteria = focus.ToList(),
            Status = status
        };
        r.Milestones.Add(m);
        return m;
    }

    private static void AddLesson(RoadmapMilestone m, int order, LessonStatus status, Guid? sessionId)
        => m.Lessons.Add(new RoadmapLesson
        {
            Id = Guid.NewGuid(),
            OrderNo = order,
            Title = $"L{order}",
            Status = status,
            SessionId = sessionId
        });

    private static (RoadmapReportService svc, Mock<IAiServiceRoadmapGenerator> gen) Svc(
        TestDb t, RoadmapSummaryAiResult? aiResult = null, Exception? aiThrows = null)
    {
        var gen = new Mock<IAiServiceRoadmapGenerator>();
        var setup = gen.Setup(g => g.SummarizeRoadmapAsync(
            It.IsAny<string>(), It.IsAny<string>(),
            It.IsAny<IReadOnlyList<RoadmapCriteriaProgress>>(), It.IsAny<CancellationToken>()));
        if (aiThrows is not null) setup.ThrowsAsync(aiThrows);
        else setup.ReturnsAsync(aiResult ?? new RoadmapSummaryAiResult([], [], [], null));

        var svc = new RoadmapReportService(
            t.Db, gen.Object, Options.Create(new RoadmapOptions()),
            NullLogger<RoadmapReportService>.Instance);
        return (svc, gen);
    }

    // ── (1) mọi lesson của milestone Done → milestone Completed + improvement khớp tính tay ──
    // Roadmap 2 mile: chỉ mile 1 xong (mile 2 còn Theory) → roadmap vẫn Active.
    [Fact]
    public async Task AllLessonsDone_CompletesMilestone_WithImprovementDeltaVsBaseline()
    {
        using var t = new TestDb();
        var user = Guid.NewGuid();

        // baseline Clarity 40 / Depth 30 (lúc tạo roadmap).
        var s1 = AddScoredSession(t, user, ("Clarity", 60m), ("Depth", 50m));
        var s2 = AddScoredSession(t, user, ("Clarity", 70m), ("Depth", 60m));

        var r = NewRoadmap(user, new Dictionary<string, decimal> { ["Clarity"] = 40m, ["Depth"] = 30m });
        var m1 = AddMilestone(r, 1, MilestoneStatus.InProgress, "Clarity", "Depth");
        AddLesson(m1, 1, LessonStatus.Done, s1);
        AddLesson(m1, 2, LessonStatus.Done, s2);
        var m2 = AddMilestone(r, 2, MilestoneStatus.Pending, "Clarity");
        AddLesson(m2, 1, LessonStatus.Theory, null);
        t.Db.Roadmaps.Add(r);
        await t.Db.SaveChangesAsync();

        var (svc, gen) = Svc(t);
        await svc.OnLessonDoneAsync(s2);

        var db = t.NewContext();
        var mile1 = await db.RoadmapMilestones.AsNoTracking().FirstAsync(m => m.Id == m1.Id);
        Assert.Equal(MilestoneStatus.Completed, mile1.Status);
        Assert.NotNull(mile1.CompletedAt);
        Assert.NotNull(mile1.Improvement);
        // avg mile1: Clarity (60+70)/2=65, Depth (50+60)/2=55 ; delta vs baseline: 65-40=25, 55-30=25.
        Assert.Equal(25m, mile1.Improvement!["Clarity"]);
        Assert.Equal(25m, mile1.Improvement!["Depth"]);

        // Mile 2 chưa xong → roadmap vẫn Active.
        var roadmap = await db.Roadmaps.AsNoTracking().FirstAsync(x => x.Id == r.Id);
        Assert.Equal(RoadmapStatus.Active, roadmap.Status);
        Assert.Null(roadmap.FinalReport);
        // roadmap chưa đóng → KHÔNG gọi AI.
        gen.Verify(g => g.SummarizeRoadmapAsync(
            It.IsAny<string>(), It.IsAny<string>(),
            It.IsAny<IReadOnlyList<RoadmapCriteriaProgress>>(), It.IsAny<CancellationToken>()), Times.Never);
    }

    // baseline null cho mile 1 → improvement = điểm đạt (không có delta).
    [Fact]
    public async Task Milestone1_NullBaseline_ImprovementShowsAchievedScore()
    {
        using var t = new TestDb();
        var user = Guid.NewGuid();
        var s1 = AddScoredSession(t, user, ("Clarity", 72m));

        var r = NewRoadmap(user, baseline: null);
        var m1 = AddMilestone(r, 1, MilestoneStatus.InProgress, "Clarity");
        AddLesson(m1, 1, LessonStatus.Done, s1);
        var m2 = AddMilestone(r, 2, MilestoneStatus.Pending, "Clarity");
        AddLesson(m2, 1, LessonStatus.Theory, null);
        t.Db.Roadmaps.Add(r);
        await t.Db.SaveChangesAsync();

        var (svc, _) = Svc(t);
        await svc.OnLessonDoneAsync(s1);

        var mile1 = await t.NewContext().RoadmapMilestones.AsNoTracking().FirstAsync(m => m.Id == m1.Id);
        Assert.Equal(MilestoneStatus.Completed, mile1.Status);
        Assert.Equal(72m, mile1.Improvement!["Clarity"]);   // không có mốc → hiện điểm đạt
    }

    // mile N (N≥2) so mile N−1.
    [Fact]
    public async Task Milestone2_ImprovementDeltaVsPreviousMilestone()
    {
        using var t = new TestDb();
        var user = Guid.NewGuid();
        var s1 = AddScoredSession(t, user, ("Clarity", 50m));   // mile 1
        var s2 = AddScoredSession(t, user, ("Clarity", 80m));   // mile 2

        var r = NewRoadmap(user, new Dictionary<string, decimal> { ["Clarity"] = 20m });
        var m1 = AddMilestone(r, 1, MilestoneStatus.Completed, "Clarity");
        AddLesson(m1, 1, LessonStatus.Done, s1);
        var m2 = AddMilestone(r, 2, MilestoneStatus.InProgress, "Clarity");
        AddLesson(m2, 1, LessonStatus.Done, s2);
        t.Db.Roadmaps.Add(r);
        await t.Db.SaveChangesAsync();

        var (svc, _) = Svc(t, new RoadmapSummaryAiResult([], [], [], null));
        await svc.OnLessonDoneAsync(s2);

        var mile2 = await t.NewContext().RoadmapMilestones.AsNoTracking().FirstAsync(m => m.Id == m2.Id);
        Assert.Equal(MilestoneStatus.Completed, mile2.Status);
        // delta mile2(80) − mile1(50) = 30 (KHÔNG so baseline 20).
        Assert.Equal(30m, mile2.Improvement!["Clarity"]);
    }

    // ── (2) mọi milestone Completed → roadmap Completed + final_report có radar/levelEvaluation + AI comment ──
    [Fact]
    public async Task AllMilestonesCompleted_CompletesRoadmap_SnapshotsFinalReport()
    {
        using var t = new TestDb();
        var user = Guid.NewGuid();
        // Clarity 80 (đạt Junior 60), Depth 40 (dưới ngưỡng).
        var s1 = AddScoredSession(t, user, ("Clarity", 80m), ("Depth", 40m));

        var r = NewRoadmap(user, new Dictionary<string, decimal> { ["Clarity"] = 50m, ["Depth"] = 20m });
        var m1 = AddMilestone(r, 1, MilestoneStatus.InProgress, "Clarity", "Depth");
        AddLesson(m1, 1, LessonStatus.Done, s1);
        t.Db.Roadmaps.Add(r);
        await t.Db.SaveChangesAsync();

        var ai = new RoadmapSummaryAiResult(
            ["Clarity tốt"], ["Depth yếu"], ["Luyện thêm Depth"], "Tiến bộ rõ ở Clarity.");
        var (svc, gen) = Svc(t, ai);
        await svc.OnLessonDoneAsync(s1);

        var db = t.NewContext();
        var roadmap = await db.Roadmaps.AsNoTracking().FirstAsync(x => x.Id == r.Id);
        Assert.Equal(RoadmapStatus.Completed, roadmap.Status);
        Assert.NotNull(roadmap.CompletedAt);
        Assert.Equal("Tiến bộ rõ ở Clarity.", roadmap.OverallComment);
        Assert.False(string.IsNullOrEmpty(roadmap.FinalReport));

        var report = JsonSerializer.Deserialize<RoadmapReportResponse>(roadmap.FinalReport!, Json)!;
        Assert.Equal(2, report.Radar.Count);
        var clarity = report.Radar.First(c => c.Name == "Clarity");
        Assert.Equal(80m, clarity.Percentage);
        var levelClarity = report.LevelEvaluation.First(e => e.CriterionName == "Clarity");
        Assert.Equal(60, levelClarity.LevelThreshold);
        Assert.True(levelClarity.Passed);
        var levelDepth = report.LevelEvaluation.First(e => e.CriterionName == "Depth");
        Assert.False(levelDepth.Passed);
        Assert.Equal("Tiến bộ rõ ở Clarity.", report.OverallComment);
        Assert.Contains("Clarity tốt", report.Strengths);

        // AI được gọi đúng 1 lần với tiến độ tiêu chí (startPct = baseline, endPct = radar).
        gen.Verify(g => g.SummarizeRoadmapAsync(
            "BE", "Junior",
            It.Is<IReadOnlyList<RoadmapCriteriaProgress>>(p =>
                p.Any(x => x.CriterionName == "Clarity" && x.StartPct == 50m && x.EndPct == 80m && x.Passed)),
            It.IsAny<CancellationToken>()), Times.Once);
    }

    // ── (3) AI /summarize-roadmap throw → roadmap vẫn Completed, comment null, kết luận rỗng ──
    [Fact]
    public async Task RoadmapCompletion_AiThrows_StillCompleted_CommentNull()
    {
        using var t = new TestDb();
        var user = Guid.NewGuid();
        var s1 = AddScoredSession(t, user, ("Clarity", 90m));

        var r = NewRoadmap(user, new Dictionary<string, decimal> { ["Clarity"] = 50m });
        var m1 = AddMilestone(r, 1, MilestoneStatus.InProgress, "Clarity");
        AddLesson(m1, 1, LessonStatus.Done, s1);
        t.Db.Roadmaps.Add(r);
        await t.Db.SaveChangesAsync();

        var (svc, _) = Svc(t, aiThrows: new AiServiceException("AIService /summarize-roadmap sập"));
        await svc.OnLessonDoneAsync(s1);

        var db = t.NewContext();
        var roadmap = await db.Roadmaps.AsNoTracking().FirstAsync(x => x.Id == r.Id);
        Assert.Equal(RoadmapStatus.Completed, roadmap.Status);   // vẫn đóng
        Assert.Null(roadmap.OverallComment);

        var report = JsonSerializer.Deserialize<RoadmapReportResponse>(roadmap.FinalReport!, Json)!;
        Assert.Single(report.Radar);   // radar vẫn tính (không phụ thuộc AI)
        Assert.Empty(report.Strengths);
        Assert.Empty(report.Weaknesses);
        Assert.Empty(report.Improvements);
        Assert.Null(report.OverallComment);
    }

    // ── (4a) GET report Active → interim (radar + levelEvaluation; kết luận rỗng; KHÔNG gọi AI) ──
    [Fact]
    public async Task GetReport_Active_ReturnsInterim_NoAiCall()
    {
        using var t = new TestDb();
        var user = Guid.NewGuid();
        var s1 = AddScoredSession(t, user, ("Clarity", 70m), ("Depth", 30m));

        var r = NewRoadmap(user, null);
        var m1 = AddMilestone(r, 1, MilestoneStatus.InProgress, "Clarity", "Depth");
        AddLesson(m1, 1, LessonStatus.Practicing, s1);   // đang luyện, roadmap Active
        t.Db.Roadmaps.Add(r);
        await t.Db.SaveChangesAsync();

        var (svc, gen) = Svc(t);
        var report = await svc.GetReportAsync(user, r.Id);

        Assert.NotNull(report);
        Assert.Equal(2, report!.Radar.Count);
        Assert.Equal(70m, report.Radar.First(c => c.Name == "Clarity").Percentage);
        Assert.True(report.LevelEvaluation.First(e => e.CriterionName == "Clarity").Passed);   // 70 ≥ 60
        Assert.False(report.LevelEvaluation.First(e => e.CriterionName == "Depth").Passed);    // 30 < 60
        Assert.Empty(report.Strengths);
        Assert.Null(report.OverallComment);

        // interim KHÔNG gọi AI.
        gen.Verify(g => g.SummarizeRoadmapAsync(
            It.IsAny<string>(), It.IsAny<string>(),
            It.IsAny<IReadOnlyList<RoadmapCriteriaProgress>>(), It.IsAny<CancellationToken>()), Times.Never);
    }

    // ── (4b) GET report Completed → đọc snapshot, KHÔNG tính lại ──
    [Fact]
    public async Task GetReport_Completed_ReturnsSnapshot_NotRecomputed()
    {
        using var t = new TestDb();
        var user = Guid.NewGuid();
        // Session gắn có Clarity 10 — nếu tính lại radar sẽ ra 10, KHÁC snapshot bên dưới (99).
        var s1 = AddScoredSession(t, user, ("Clarity", 10m));

        var snapshot = new RoadmapReportResponse(
            [new CriterionScoreResponse(Guid.NewGuid(), "Clarity", 4.95m, 5, 99m, 1m)],
            [new RoadmapLevelEvaluationResponse("Clarity", 99m, 60, true)],
            ["SNAP mạnh"], ["SNAP yếu"], ["SNAP cải thiện"], "SNAP nhận xét");
        var snapshotJson = JsonSerializer.Serialize(snapshot, Json);

        var r = NewRoadmap(user, null);
        r.Status = RoadmapStatus.Completed;
        r.FinalReport = snapshotJson;
        r.OverallComment = "SNAP nhận xét";
        r.CompletedAt = DateTime.UtcNow;
        var m1 = AddMilestone(r, 1, MilestoneStatus.Completed, "Clarity");
        AddLesson(m1, 1, LessonStatus.Done, s1);
        t.Db.Roadmaps.Add(r);
        await t.Db.SaveChangesAsync();

        var (svc, gen) = Svc(t);
        var report = await svc.GetReportAsync(user, r.Id);

        Assert.NotNull(report);
        Assert.Equal(99m, report!.Radar.Single().Percentage);   // snapshot (99), KHÔNG phải tính lại (10)
        Assert.Equal("SNAP nhận xét", report.OverallComment);
        Assert.Contains("SNAP mạnh", report.Strengths);

        // Đọc snapshot KHÔNG gọi AI.
        gen.Verify(g => g.SummarizeRoadmapAsync(
            It.IsAny<string>(), It.IsAny<string>(),
            It.IsAny<IReadOnlyList<RoadmapCriteriaProgress>>(), It.IsAny<CancellationToken>()), Times.Never);
    }

    // ── (5) owner-only: khác chủ → 403; roadmap không tồn tại → 404 (qua controller) ──
    [Fact]
    public async Task GetReport_Stranger_403_Missing_404()
    {
        using var t = new TestDb();
        var owner = Guid.NewGuid();
        var stranger = Guid.NewGuid();

        var r = NewRoadmap(owner, null);
        AddMilestone(r, 1, MilestoneStatus.Pending, "Clarity");
        t.Db.Roadmaps.Add(r);
        await t.Db.SaveChangesAsync();

        var (svc, _) = Svc(t);

        // stranger → 403
        var strangerCtrl = Controller(svc, stranger);
        var forbidden = Assert.IsType<ObjectResult>(await strangerCtrl.GetReport(r.Id, default));
        Assert.Equal(StatusCodes.Status403Forbidden, forbidden.StatusCode);

        // owner + roadmap lạ → 404
        var ownerCtrl = Controller(svc, owner);
        Assert.IsType<NotFoundObjectResult>(await ownerCtrl.GetReport(Guid.NewGuid(), default));
    }

    // ── (6) integration: session Scored qua notifier THẬT → lesson Done → roadmap Completed ──
    [Fact]
    public async Task SessionScored_ViaNotifier_CompletesMilestoneAndRoadmap()
    {
        using var t = new TestDb();
        var user = Guid.NewGuid();

        // session Scored + breakdown (đường notifier: BC9 đã ghi session_criterion_scores).
        var s1 = AddScoredSession(t, user, ("Clarity", 88m));

        var r = NewRoadmap(user, new Dictionary<string, decimal> { ["Clarity"] = 40m });
        var m1 = AddMilestone(r, 1, MilestoneStatus.InProgress, "Clarity");
        AddLesson(m1, 1, LessonStatus.Practicing, s1);   // đang luyện → notifier sẽ đánh Done
        t.Db.Roadmaps.Add(r);
        await t.Db.SaveChangesAsync();

        var gen = new Mock<IAiServiceRoadmapGenerator>();
        gen.Setup(g => g.SummarizeRoadmapAsync(
                It.IsAny<string>(), It.IsAny<string>(),
                It.IsAny<IReadOnlyList<RoadmapCriteriaProgress>>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(new RoadmapSummaryAiResult([], [], [], "OK"));
        var report = TestDb.RoadmapReport(t.Db, gen.Object);

        var notifier = TestDb.Notifier(t.Db, roadmapReport: report);

        await notifier.NotifySessionScoredAsync(s1);

        var db = t.NewContext();
        var lesson = await db.RoadmapLessons.AsNoTracking().FirstAsync(l => l.SessionId == s1);
        Assert.Equal(LessonStatus.Done, lesson.Status);
        var mile = await db.RoadmapMilestones.AsNoTracking().FirstAsync(m => m.Id == m1.Id);
        Assert.Equal(MilestoneStatus.Completed, mile.Status);
        var roadmap = await db.Roadmaps.AsNoTracking().FirstAsync(x => x.Id == r.Id);
        Assert.Equal(RoadmapStatus.Completed, roadmap.Status);
        Assert.Equal("OK", roadmap.OverallComment);
    }

    private static RoadmapsController Controller(IRoadmapReportService reportService, Guid userId)
    {
        var controller = new RoadmapsController(
            new Mock<IRoadmapService>().Object,
            new Mock<IRoadmapLessonService>().Object,
            reportService,
            NullLogger<RoadmapsController>.Instance);
        var principal = new ClaimsPrincipal(new ClaimsIdentity(
            [new Claim(ClaimTypes.NameIdentifier, userId.ToString())], "test"));
        controller.ControllerContext = new ControllerContext
        {
            HttpContext = new DefaultHttpContext { User = principal }
        };
        return controller;
    }
}
