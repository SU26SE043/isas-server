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
        => AddScoredSessionAt(t, cand, DateTime.UtcNow, scores);

    // Như trên nhưng GHIM mốc thời gian chấm (`session_criterion_scores.created_at`) — đây là khoá
    // sắp xếp của cả cửa sổ "3 buổi gần nhất" (radar) lẫn thứ tự đường xu hướng (progress). Để
    // DateTime.UtcNow cho nhiều buổi trong cùng một test thì các mốc sát nhau tới mức có thể trùng
    // tick ⇒ thứ tự phụ thuộc tiebreak Guid, test sẽ nhấp nháy.
    private static Guid AddScoredSessionAt(
        TestDb t, Guid cand, DateTime at, params (string name, decimal pct)[] scores)
    {
        var session = TestDb.Session(cand, SessionStatus.Scored, createdAt: at);
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
                CreatedAt = at
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

    private static void AddLesson(
        RoadmapMilestone m, int order, LessonStatus status, Guid? sessionId, string? title = null)
        => m.Lessons.Add(new RoadmapLesson
        {
            Id = Guid.NewGuid(),
            OrderNo = order,
            Title = title ?? $"L{order}",
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
            t.Db, gen.Object, TestDb.Thresholds(t.Db),
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

    // ⚠ ĐẢO TIỀN ĐỀ CÓ CHỦ ĐÍCH (tên cũ: Milestone1_NullBaseline_ImprovementShowsAchievedScore).
    // Bản cũ khẳng định "không có mốc → hiện điểm đạt" và coi đó là hành vi ĐÚNG — thực ra đó là
    // BUG: `improvement` lên UI dưới nhãn "tiến độ" (delta), nhét điểm tuyệt đối (72m) vào đó là
    // nói cho người học một điều SAI theo hướng CÓ LỢI (trông như tiến bộ +72% trong khi bài chỉ
    // đạt 72/100 điểm). Sự cố thật trên production: milestone Completed lưu
    // {"Ngữ pháp & dùng từ": 25.01}. Tiêu chí không có mốc → BỎ QUA, không có tiêu chí nào còn lại
    // → Improvement = null (không phải dictionary rỗng).
    [Fact]
    public async Task Milestone1_NullBaseline_ImprovementIsNull()
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
        Assert.Null(mile1.Improvement);
    }

    // mile 2 KHÔNG có baseline (null từ lúc tạo roadmap) nhưng mile 1 CÓ điểm → vẫn phải so đúng
    // với mile 1 (không phải rơi về baseline null rồi trả rỗng/lỗi). Đây chính là ca "milestone 2
    // trở đi lấy reference từ milestone trước" đang ĐÚNG — không được để sửa lỗi (1) làm hỏng nó.
    [Fact]
    public async Task Milestone2_NoBaseline_ButMilestone1HasScores_StillUsesMilestone1AsReference()
    {
        using var t = new TestDb();
        var user = Guid.NewGuid();
        var s1 = AddScoredSession(t, user, ("Clarity", 50m));   // mile 1
        var s2 = AddScoredSession(t, user, ("Clarity", 80m));   // mile 2

        var r = NewRoadmap(user, baseline: null);   // KHÔNG có baseline
        var m1 = AddMilestone(r, 1, MilestoneStatus.Completed, "Clarity");
        AddLesson(m1, 1, LessonStatus.Done, s1);
        var m2 = AddMilestone(r, 2, MilestoneStatus.InProgress, "Clarity");
        AddLesson(m2, 1, LessonStatus.Done, s2);
        t.Db.Roadmaps.Add(r);
        await t.Db.SaveChangesAsync();

        var (svc, _) = Svc(t);
        await svc.OnLessonDoneAsync(s2);

        var mile2 = await t.NewContext().RoadmapMilestones.AsNoTracking().FirstAsync(m => m.Id == m2.Id);
        Assert.Equal(MilestoneStatus.Completed, mile2.Status);
        // delta mile2(80) − mile1(50) = 30, dù roadmap.Baseline null.
        Assert.Equal(30m, mile2.Improvement!["Clarity"]);
    }

    // baseline chỉ khai 2/7 tiêu chí của milestone → improvement CHỈ có đúng 2 entry, 5 tiêu chí
    // còn lại (không có mốc) bị BỎ QUA thay vì nhét điểm tuyệt đối vào slot delta.
    [Fact]
    public async Task BaselineCoversOnlySomeCriteria_ImprovementHasOnlyThoseEntries()
    {
        using var t = new TestDb();
        var user = Guid.NewGuid();
        var names = new[] { "C1", "C2", "C3", "C4", "C5", "C6", "C7" };
        var s1 = AddScoredSession(t, user, names.Select(n => (n, 60m)).ToArray());

        // baseline chỉ khai C1/C2 — 5 tiêu chí kia không có mốc.
        var baseline = new Dictionary<string, decimal> { ["C1"] = 40m, ["C2"] = 50m };
        var r = NewRoadmap(user, baseline);
        var m1 = AddMilestone(r, 1, MilestoneStatus.InProgress, names);
        AddLesson(m1, 1, LessonStatus.Done, s1);
        t.Db.Roadmaps.Add(r);
        await t.Db.SaveChangesAsync();

        var (svc, _) = Svc(t);
        await svc.OnLessonDoneAsync(s1);

        var mile1 = await t.NewContext().RoadmapMilestones.AsNoTracking().FirstAsync(m => m.Id == m1.Id);
        Assert.NotNull(mile1.Improvement);
        Assert.Equal(2, mile1.Improvement!.Count);
        Assert.Equal(20m, mile1.Improvement["C1"]);   // 60 − 40
        Assert.Equal(10m, mile1.Improvement["C2"]);   // 60 − 50
        foreach (var dropped in new[] { "C3", "C4", "C5", "C6", "C7" })
            Assert.False(mile1.Improvement.ContainsKey(dropped));
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

        // JSON viết TAY theo hình dạng CŨ (trước khi có `progress` / `startPercentage`), KHÔNG dựng
        // từ record hiện tại: bản ghi thật nằm trong DB là chuỗi cũ, và serialize record mới sẽ luôn
        // kèm các khoá mới ⇒ test sẽ không bao giờ chạm ca "snapshot cũ" mà nó sinh ra để bảo vệ.
        // Snapshot cố ý mang "Active": nó được chốt NGAY TRƯỚC lệnh cập nhật roadmap sang Completed
        // nên trong thực tế luôn lưu trạng thái cũ. Đường đọc phải ghi đè bằng trạng thái HIỆN TẠI.
        var snapshotJson = """
            {
              "radar": [
                { "criterionId": "11111111-1111-1111-1111-111111111111", "name": "Clarity",
                  "averageScore": 4.95, "maxScore": 5, "percentage": 99, "weight": 1 }
              ],
              "levelEvaluation": [
                { "criterionName": "Clarity", "percentage": 99, "levelThreshold": 60, "passed": true }
              ],
              "strengths": ["SNAP mạnh"],
              "weaknesses": ["SNAP yếu"],
              "improvements": ["SNAP cải thiện"],
              "overallComment": "SNAP nhận xét",
              "roadmapStatus": "Active"
            }
            """;

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
        // Snapshot cũ không có khoá `progress` ⇒ deserialize gán null vào một thuộc tính khai là
        // non-nullable. Đường đọc phải chuẩn hoá về rỗng, KHÔNG được để null lọt ra ngoài.
        Assert.NotNull(report.Progress);
        Assert.Empty(report.Progress);
        // Cũng không có `startPercentage` ⇒ null (= KHÔNG BIẾT), không được vẽ thành số.
        Assert.Null(report.Radar.Single().StartPercentage);

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

    // ══ Xu hướng gần đây: radar chốt theo TỐI ĐA 3 BUỔI GẦN NHẤT, không phải trung bình mọi buổi ══

    // ── (7) 5 buổi → chỉ 3 buổi CUỐI vào radar; 2 buổi đầu không được ảnh hưởng con số ──
    [Fact]
    public async Task Radar_ChiTinhBaBuoiGanNhat_HaiBuoiDauKhongAnhHuong()
    {
        using var t = new TestDb();
        var user = Guid.NewGuid();
        var t0 = new DateTime(2026, 8, 1, 8, 0, 0, DateTimeKind.Utc);

        // Clarity theo thời gian: 0, 0, 100, 100, 100.
        //   trung bình MỌI buổi  = 60   (con số cũ)
        //   trung bình 3 gần nhất = 100 (con số mới)
        var s1 = AddScoredSessionAt(t, user, t0, ("Clarity", 0m));
        var s2 = AddScoredSessionAt(t, user, t0.AddDays(1), ("Clarity", 0m));
        var s3 = AddScoredSessionAt(t, user, t0.AddDays(2), ("Clarity", 100m));
        var s4 = AddScoredSessionAt(t, user, t0.AddDays(3), ("Clarity", 100m));
        var s5 = AddScoredSessionAt(t, user, t0.AddDays(4), ("Clarity", 100m));

        var r = NewRoadmap(user, null);
        var m1 = AddMilestone(r, 1, MilestoneStatus.InProgress, "Clarity");
        AddLesson(m1, 1, LessonStatus.Done, s1);
        AddLesson(m1, 2, LessonStatus.Done, s2);
        AddLesson(m1, 3, LessonStatus.Done, s3);
        AddLesson(m1, 4, LessonStatus.Done, s4);
        AddLesson(m1, 5, LessonStatus.Done, s5);
        t.Db.Roadmaps.Add(r);
        await t.Db.SaveChangesAsync();

        var (svc, _) = Svc(t);
        var report = await svc.GetReportAsync(user, r.Id);

        var clarity = report!.Radar.Single();
        Assert.Equal(100m, clarity.Percentage);        // KHÔNG phải 60 (trung bình toàn cục)
        Assert.Equal(5m, clarity.AverageScore);        // điểm thô cùng cửa sổ (maxScore 5)
        Assert.Equal(0m, clarity.StartPercentage);     // mốc xuất phát = buổi ĐẦU TIÊN
        Assert.Equal(5, clarity.SessionCount);         // cỡ mẫu = tổng số buổi có chấm tiêu chí
        Assert.Equal(3, clarity.RecentCount);          // nhưng chỉ 3 buổi thực sự vào con số
    }

    // ── (8) tiêu chí mới có đúng 1 buổi → StartPercentage = null (KHÔNG BIẾT), không bịa mốc ──
    [Fact]
    public async Task Radar_MotBuoiDuyNhat_StartPercentageLaNull()
    {
        using var t = new TestDb();
        var user = Guid.NewGuid();
        var s1 = AddScoredSession(t, user, ("Clarity", 70m));

        var r = NewRoadmap(user, null);
        var m1 = AddMilestone(r, 1, MilestoneStatus.InProgress, "Clarity");
        AddLesson(m1, 1, LessonStatus.Done, s1);
        t.Db.Roadmaps.Add(r);
        await t.Db.SaveChangesAsync();

        var (svc, _) = Svc(t);
        var report = await svc.GetReportAsync(user, r.Id);

        var clarity = report!.Radar.Single();
        Assert.Null(clarity.StartPercentage);   // không có gì để so — KHÔNG lặp lại 70 thành "tiến bộ 0%"
        Assert.Equal(70m, clarity.Percentage);
        Assert.Equal(1, clarity.SessionCount);
        Assert.Equal(1, clarity.RecentCount);
    }

    // ── (9) INT-18: các nan radar KHÁC cỡ mẫu — tiêu chí nội dung chỉ được chấm ở vài buổi ──
    [Fact]
    public async Task Radar_CoMauKhacNhauGiuaCacTieuChi_SessionCountPhanAnhDung()
    {
        using var t = new TestDb();
        var user = Guid.NewGuid();
        var t0 = new DateTime(2026, 8, 1, 8, 0, 0, DateTimeKind.Utc);

        // "Giao tiếp" (cách nói) chấm ở MỌI buổi; "Thuật toán" (nội dung) chỉ ở buổi 2 và 3 —
        // đúng hình dạng INT-18: tiêu chí nội dung chỉ chấm khi câu hỏi nhắm tới nó.
        var s1 = AddScoredSessionAt(t, user, t0, ("Giao tiếp", 40m));
        var s2 = AddScoredSessionAt(t, user, t0.AddDays(1), ("Giao tiếp", 60m), ("Thuật toán", 20m));
        var s3 = AddScoredSessionAt(t, user, t0.AddDays(2), ("Giao tiếp", 80m), ("Thuật toán", 80m));
        var s4 = AddScoredSessionAt(t, user, t0.AddDays(3), ("Giao tiếp", 100m));

        var r = NewRoadmap(user, null);
        var m1 = AddMilestone(r, 1, MilestoneStatus.InProgress, "Giao tiếp", "Thuật toán");
        AddLesson(m1, 1, LessonStatus.Done, s1);
        AddLesson(m1, 2, LessonStatus.Done, s2);
        AddLesson(m1, 3, LessonStatus.Done, s3);
        AddLesson(m1, 4, LessonStatus.Done, s4);
        t.Db.Roadmaps.Add(r);
        await t.Db.SaveChangesAsync();

        var (svc, _) = Svc(t);
        var report = await svc.GetReportAsync(user, r.Id);

        var giaoTiep = report!.Radar.First(c => c.Name == "Giao tiếp");
        Assert.Equal(4, giaoTiep.SessionCount);
        Assert.Equal(3, giaoTiep.RecentCount);
        Assert.Equal(80m, giaoTiep.Percentage);       // (60+80+100)/3, buổi đầu 40 rơi ra ngoài cửa sổ
        Assert.Equal(40m, giaoTiep.StartPercentage);

        var thuatToan = report.Radar.First(c => c.Name == "Thuật toán");
        Assert.Equal(2, thuatToan.SessionCount);      // cỡ mẫu NHỎ HƠN hẳn nan bên cạnh
        Assert.Equal(2, thuatToan.RecentCount);
        Assert.Equal(50m, thuatToan.Percentage);      // (20+80)/2 — đếm theo BUỔI CÓ CHẤM, không phải mọi buổi
        Assert.Equal(20m, thuatToan.StartPercentage);
    }

    // ── (10) levelEvaluation bám cửa sổ gần đây: che-xu-hướng theo CẢ HAI chiều ──
    [Fact]
    public async Task LevelEvaluation_LatKetQuaKhiXuHuongDoiChieu()
    {
        using var t = new TestDb();
        var user = Guid.NewGuid();
        var t0 = new DateTime(2026, 8, 1, 8, 0, 0, DateTimeKind.Utc);

        // Ngưỡng Junior = 60.
        //   "Đi lên":    20,20,20,80,80 → toàn cục 44 (TRƯỢT) · 3 gần nhất (20+80+80)/3 = 60 (ĐẠT)
        //   "Đi xuống": 100,100,100,20,20 → toàn cục 68 (ĐẠT) · 3 gần nhất (100+20+20)/3 = 46,67 (TRƯỢT)
        // Vế "Đi xuống" chính là triệu chứng đo được trên dữ liệu thật: trung bình che mất đà tụt,
        // và che theo hướng CÓ LỢI cho người học.
        var pairs = new[]
        {
            (20m, 100m), (20m, 100m), (20m, 100m), (80m, 20m), (80m, 20m)
        };
        var r = NewRoadmap(user, null);
        var m1 = AddMilestone(r, 1, MilestoneStatus.InProgress, "Đi lên", "Đi xuống");
        for (var i = 0; i < pairs.Length; i++)
        {
            var sid = AddScoredSessionAt(
                t, user, t0.AddDays(i), ("Đi lên", pairs[i].Item1), ("Đi xuống", pairs[i].Item2));
            AddLesson(m1, i + 1, LessonStatus.Done, sid);
        }
        t.Db.Roadmaps.Add(r);
        await t.Db.SaveChangesAsync();

        var (svc, _) = Svc(t);
        var report = await svc.GetReportAsync(user, r.Id);

        var up = report!.LevelEvaluation.First(e => e.CriterionName == "Đi lên");
        Assert.Equal(60m, up.Percentage);
        Assert.True(up.Passed);                       // toàn cục 44 sẽ TRƯỢT — cửa sổ gần đây lật lại

        var down = report.LevelEvaluation.First(e => e.CriterionName == "Đi xuống");
        Assert.Equal(46.67m, down.Percentage);
        Assert.False(down.Passed);                    // toàn cục 68 sẽ ĐẠT — trung bình đang che đà tụt
    }

    // ── (11) progress: đúng thứ tự thời gian + điểm tổng của CHÍNH buổi đó ──
    [Fact]
    public async Task Progress_XepTheoThoiGian_VaTinhDiemTongTungBuoi()
    {
        using var t = new TestDb();
        var user = Guid.NewGuid();
        var t0 = new DateTime(2026, 8, 1, 8, 0, 0, DateTimeKind.Utc);

        // Seed NGƯỢC thứ tự thời gian (buổi mới nhất thêm vào DB trước) để chắc chắn thứ tự đầu ra
        // đến từ mốc thời gian chứ không phải thứ tự chèn.
        var sLast = AddScoredSessionAt(t, user, t0.AddDays(2), ("Clarity", 90m), ("Depth", 70m));
        var sMid = AddScoredSessionAt(t, user, t0.AddDays(1), ("Clarity", 60m), ("Depth", 40m));
        var sFirst = AddScoredSessionAt(t, user, t0, ("Clarity", 20m), ("Depth", 20m));

        var r = NewRoadmap(user, null);
        var m1 = AddMilestone(r, 1, MilestoneStatus.InProgress, "Clarity", "Depth");
        AddLesson(m1, 1, LessonStatus.Done, sFirst, "Bài mở đầu");
        AddLesson(m1, 2, LessonStatus.Done, sMid, "Bài giữa");
        AddLesson(m1, 3, LessonStatus.Done, sLast, "Bài cuối");
        t.Db.Roadmaps.Add(r);
        await t.Db.SaveChangesAsync();

        var (svc, _) = Svc(t);
        var report = await svc.GetReportAsync(user, r.Id);

        Assert.Equal(3, report!.Progress.Count);
        Assert.Equal([1, 2, 3], report.Progress.Select(p => p.Order).ToArray());
        Assert.Equal(
            ["Bài mở đầu", "Bài giữa", "Bài cuối"],
            report.Progress.Select(p => p.LessonTitle).ToArray());
        Assert.Equal(
            [t0, t0.AddDays(1), t0.AddDays(2)],
            report.Progress.Select(p => p.CompletedAt).ToArray());

        // Điểm tổng buổi = trung bình cộng tiêu chí CỦA CHÍNH buổi đó (không phải tích luỹ).
        Assert.Equal(20m, report.Progress[0].OverallPercentage);   // (20+20)/2
        Assert.Equal(50m, report.Progress[1].OverallPercentage);   // (60+40)/2
        Assert.Equal(80m, report.Progress[2].OverallPercentage);   // (90+70)/2

        var last = report.Progress[2].Scores;
        Assert.Equal(["Clarity", "Depth"], last.Select(c => c.Name).ToArray());
        Assert.Equal(90m, last.First(c => c.Name == "Clarity").Percentage);
    }

    // ── (12) snapshot cũ (thiếu `progress`) đọc ra rỗng, KHÔNG ném ──
    // Ca riêng khỏi (4b): ở đây snapshot KHÔNG có cả `radar` lẫn `progress` — hình dạng tối thiểu mà
    // một bản ghi cũ vẫn có thể mang, và là ca dễ ném nhất.
    [Fact]
    public async Task GetReport_SnapshotCu_ThieuProgress_TraVeRong_KhongNem()
    {
        using var t = new TestDb();
        var user = Guid.NewGuid();

        var snapshotJson = """
            {
              "radar": [],
              "levelEvaluation": [],
              "strengths": [],
              "weaknesses": [],
              "improvements": [],
              "overallComment": "Bản cũ",
              "roadmapStatus": "Active"
            }
            """;

        var r = NewRoadmap(user, null);
        r.Status = RoadmapStatus.Completed;
        r.FinalReport = snapshotJson;
        r.CompletedAt = DateTime.UtcNow;
        AddMilestone(r, 1, MilestoneStatus.Completed, "Clarity");
        t.Db.Roadmaps.Add(r);
        await t.Db.SaveChangesAsync();

        var (svc, _) = Svc(t);

        // Trước bản vá: `progress` deserialize ra null cho một thuộc tính khai non-nullable ⇒ NRE.
        var report = await svc.GetReportAsync(user, r.Id);

        Assert.NotNull(report);
        Assert.NotNull(report!.Progress);
        Assert.Empty(report.Progress);
        Assert.Equal("Bản cũ", report.OverallComment);
        Assert.Equal(nameof(RoadmapStatus.Completed), report.RoadmapStatus);   // status ghi đè vẫn chạy
        // Serialize được = không có null lọt ra ngoài hợp đồng.
        Assert.False(string.IsNullOrEmpty(JsonSerializer.Serialize(report, Json)));
    }

    // ── (13) StartPct gửi AI: baseline THẮNG buổi đầu; thiếu baseline → rơi về buổi đầu; không có → null ──
    [Fact]
    public async Task SummarizeRoadmap_StartPct_UuTienBaseline_RoiVeBuoiDau_RoiVeNull()
    {
        using var t = new TestDb();
        var user = Guid.NewGuid();
        var t0 = new DateTime(2026, 8, 1, 8, 0, 0, DateTimeKind.Utc);

        var s1 = AddScoredSessionAt(t, user, t0, ("Clarity", 60m), ("Depth", 30m));
        var s2 = AddScoredSessionAt(t, user, t0.AddDays(1),
            ("Clarity", 80m), ("Depth", 50m), ("Solo", 90m));

        // Baseline CHỈ có Clarity → Depth phải rơi về buổi đầu, Solo (1 buổi, không baseline) → null.
        var r = NewRoadmap(user, new Dictionary<string, decimal> { ["Clarity"] = 40m });
        var m1 = AddMilestone(r, 1, MilestoneStatus.InProgress, "Clarity", "Depth", "Solo");
        AddLesson(m1, 1, LessonStatus.Done, s1);
        AddLesson(m1, 2, LessonStatus.Done, s2);
        t.Db.Roadmaps.Add(r);
        await t.Db.SaveChangesAsync();

        var (svc, gen) = Svc(t);
        IReadOnlyList<RoadmapCriteriaProgress>? sent = null;
        gen.Setup(g => g.SummarizeRoadmapAsync(
                It.IsAny<string>(), It.IsAny<string>(),
                It.IsAny<IReadOnlyList<RoadmapCriteriaProgress>>(), It.IsAny<CancellationToken>()))
            .Callback<string, string, IReadOnlyList<RoadmapCriteriaProgress>, CancellationToken>(
                (_, _, p, _) => sent = p)
            .ReturnsAsync(new RoadmapSummaryAiResult([], [], [], null));

        await svc.OnLessonDoneAsync(s2);

        Assert.NotNull(sent);
        // ① baseline lúc TẠO lộ trình thắng — KHÔNG được lấy 60 (buổi đầu trong lộ trình).
        Assert.Equal(40m, sent!.First(x => x.CriterionName == "Clarity").StartPct);
        // ② không có baseline cho tiêu chí này → dự phòng bằng buổi ĐẦU TIÊN trong lộ trình.
        Assert.Equal(30m, sent.First(x => x.CriterionName == "Depth").StartPct);
        // ③ không baseline, chỉ 1 buổi → null. KHÔNG lấp bằng 0 (trông như tiến bộ vượt bậc)
        //    cũng không lấp bằng EndPct (trông như không tiến bộ) — prompt có nhánh "chưa có baseline".
        Assert.Null(sent.First(x => x.CriterionName == "Solo").StartPct);

        // EndPct bám cửa sổ gần đây (≤3 buổi nên ở đây là trung bình cả 2 buổi).
        Assert.Equal(70m, sent.First(x => x.CriterionName == "Clarity").EndPct);
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
