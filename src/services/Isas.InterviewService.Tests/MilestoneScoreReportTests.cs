using System.Security.Claims;
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

/// <summary>
/// PHẦN TÍNH đứng sau con số delta của một chặng —
/// <c>GET /roadmaps/{id}/milestones/{mid}/score-report</c>.
///
/// <para>Bất biến quan trọng nhất, và cũng là lý do tính năng tồn tại: <b>phần tính phải cộng ra
/// đúng con số ở tiêu đề</b>. Nếu hai bên lệch nhau thì tính năng phản tác dụng — người học mất
/// niềm tin vào cả con số lẫn phần giải thích.</para>
/// </summary>
public class MilestoneScoreReportTests
{
    // ── Builders (bản gọn của RoadmapReportTests, đủ cho phần tính) ──────────────────────
    private static Guid AddScoredSessionAt(
        TestDb t, Guid cand, DateTime at, params (string name, decimal pct)[] scores)
    {
        var session = TestDb.Session(cand, SessionStatus.Scored, createdAt: at);
        t.Db.PracticeSessions.Add(session);

        foreach (var (name, pct) in scores)
        {
            // DÙNG LẠI tiêu chí cùng tên nếu đã seed — đúng như production (mọi buổi cùng
            // nghề/ngôn ngữ chia sẻ MỘT bộ tiêu chí), và báo cáo gom theo TÊN nên id trùng mới là ca thật.
            var criterion = t.Db.RubricCriteria.Local
                    .FirstOrDefault(c => c.Name == name && c.CandidateId == null
                                         && c.JobCategory == JobCategory.BE)
                ?? t.Db.RubricCriteria
                    .FirstOrDefault(c => c.Name == name && c.CandidateId == null
                                         && c.JobCategory == JobCategory.BE);
            if (criterion is null)
            {
                criterion = TestDb.Criterion(JobCategory.BE, name: name);
                t.Db.RubricCriteria.Add(criterion);
            }

            t.Db.SessionCriterionScores.Add(new SessionCriterionScore
            {
                Id = Guid.NewGuid(),
                SessionId = session.Id,
                CriterionId = criterion.Id,
                CriterionName = name,
                AverageScore = Math.Round(pct / 20m, 2),
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
            Level = RoadmapLevel.Junior,
            Baseline = baseline,
            Status = RoadmapStatus.Active,
            CreatedAt = DateTime.UtcNow
        };

    private static RoadmapMilestone AddMilestone(Roadmap r, int order, MilestoneStatus status)
    {
        var m = new RoadmapMilestone
        {
            Id = Guid.NewGuid(),
            OrderNo = order,
            Title = $"M{order}",
            FocusCriteria = [],
            Status = status
        };
        r.Milestones.Add(m);
        return m;
    }

    private static RoadmapLesson AddLesson(
        RoadmapMilestone m, int order, LessonStatus status, Guid? sessionId, string? title = null)
    {
        var l = new RoadmapLesson
        {
            Id = Guid.NewGuid(),
            OrderNo = order,
            Title = title ?? $"L{order}",
            Status = status,
            SessionId = sessionId
        };
        m.Lessons.Add(l);
        return l;
    }

    private static RoadmapReportService Svc(TestDb t)
    {
        var gen = new Mock<IAiServiceRoadmapGenerator>();
        gen.Setup(g => g.SummarizeRoadmapAsync(
                It.IsAny<string>(), It.IsAny<string>(),
                It.IsAny<IReadOnlyList<RoadmapCriteriaProgress>>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(new RoadmapSummaryAiResult([], [], [], null));
        return new RoadmapReportService(
            t.Db, gen.Object, Options.Create(new RoadmapOptions()),
            NullLogger<RoadmapReportService>.Instance);
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

    private static MilestoneScoreCriterionResponse Crit(MilestoneScoreReportResponse r, string name)
        => r.Criteria.Single(c => c.Name == name);

    // ══ BẤT BIẾN GỐC ═══════════════════════════════════════════════════════════════════════
    // Phần tính phải CỘNG RA đúng con số hiển thị: trung bình các buổi liệt kê == điểm chặng,
    // và current − reference == delta. Đây là lý do tồn tại của cả tính năng.
    [Fact]
    public async Task PhanTinh_CongRaDungConSoHienThi_VaDeltaKhopHieuSo()
    {
        using var t = new TestDb();
        var user = Guid.NewGuid();

        // Chặng 1: Giao tiếp (60+80)/2 = 70. Chặng 2: (60+40)/2 = 50 ⇒ delta −20 (ca thật của user).
        var m1s1 = AddScoredSessionAt(t, user, new DateTime(2026, 8, 1, 0, 0, 0, DateTimeKind.Utc), ("Giao tiếp", 60m));
        var m1s2 = AddScoredSessionAt(t, user, new DateTime(2026, 8, 2, 0, 0, 0, DateTimeKind.Utc), ("Giao tiếp", 80m));
        var m2s1 = AddScoredSessionAt(t, user, new DateTime(2026, 8, 3, 0, 0, 0, DateTimeKind.Utc), ("Giao tiếp", 60m));
        var m2s2 = AddScoredSessionAt(t, user, new DateTime(2026, 8, 4, 0, 0, 0, DateTimeKind.Utc), ("Giao tiếp", 40m));

        var r = NewRoadmap(user, null);
        var m1 = AddMilestone(r, 1, MilestoneStatus.Completed);
        AddLesson(m1, 1, LessonStatus.Done, m1s1);
        AddLesson(m1, 2, LessonStatus.Done, m1s2);
        var m2 = AddMilestone(r, 2, MilestoneStatus.InProgress);
        AddLesson(m2, 1, LessonStatus.Done, m2s1);
        AddLesson(m2, 2, LessonStatus.Done, m2s2);
        t.Db.Roadmaps.Add(r);
        await t.Db.SaveChangesAsync();

        var report = await Svc(t).GetMilestoneScoreReportAsync(user, r.Id, m2.Id);

        Assert.NotNull(report);
        var c = Crit(report!, "Giao tiếp");

        // ① Trung bình các buổi LIỆT KÊ == con số chặng.
        Assert.Equal(2, c.CurrentSessions.Count);
        Assert.Equal(
            Math.Round(c.CurrentSessions.Average(s => s.Percentage), 2),
            c.CurrentAveragePercentage);
        Assert.Equal(50m, c.CurrentAveragePercentage);

        // ② Mốc cũng cộng ra từ chính các buổi của nó.
        Assert.Equal(2, c.ReferenceSessions.Count);
        Assert.Equal(
            Math.Round(c.ReferenceSessions.Average(s => s.Percentage), 2),
            c.ReferenceAveragePercentage);
        Assert.Equal(70m, c.ReferenceAveragePercentage);

        // ③ delta == hiệu hai con số ở trên (không phải một phép tính thứ hai ở đâu khác).
        Assert.Equal(c.CurrentAveragePercentage - c.ReferenceAveragePercentage, c.DeltaPct);
        Assert.Equal(-20m, c.DeltaPct);

        Assert.Equal(MilestoneScoreReference.PreviousMilestone, report.ComparedWith);
        Assert.Equal("M1", report.ComparedWithTitle);
    }

    // Chặng ĐÃ CHỐT SỔ: snapshot phải khớp TỪNG con số với improvement lên tiêu đề. Hai cột ghi
    // trong cùng một ExecuteUpdate từ cùng một vòng lặp ⇒ không thể lệch; test khoá điều đó.
    [Fact]
    public async Task ChotSo_SnapshotKhopTungConSoVoiImprovementLenTieuDe()
    {
        using var t = new TestDb();
        var user = Guid.NewGuid();

        var s1 = AddScoredSessionAt(t, user, new DateTime(2026, 8, 1, 0, 0, 0, DateTimeKind.Utc), ("Clarity", 60m), ("Depth", 50m));
        var s2 = AddScoredSessionAt(t, user, new DateTime(2026, 8, 2, 0, 0, 0, DateTimeKind.Utc), ("Clarity", 70m), ("Depth", 60m));

        var r = NewRoadmap(user, new Dictionary<string, decimal> { ["Clarity"] = 40m, ["Depth"] = 30m });
        var m1 = AddMilestone(r, 1, MilestoneStatus.InProgress);
        AddLesson(m1, 1, LessonStatus.Done, s1);
        AddLesson(m1, 2, LessonStatus.Done, s2);
        var m2 = AddMilestone(r, 2, MilestoneStatus.Pending);
        AddLesson(m2, 1, LessonStatus.Theory, null);
        t.Db.Roadmaps.Add(r);
        await t.Db.SaveChangesAsync();

        await Svc(t).OnLessonDoneAsync(s2);

        var db = t.NewContext();
        var mile = await db.RoadmapMilestones.AsNoTracking().FirstAsync(m => m.Id == m1.Id);
        Assert.Equal(MilestoneStatus.Completed, mile.Status);
        Assert.NotNull(mile.ScoreSnapshot);   // phần tính được LƯU, không tính lại lúc đọc

        var report = await Svc(t).GetMilestoneScoreReportAsync(user, r.Id, m1.Id);
        Assert.NotNull(report);
        Assert.Equal(MilestoneScoreSource.Snapshot, report!.Source);
        Assert.Equal(MilestoneScoreReference.Baseline, report.ComparedWith);

        // Từng tiêu chí: delta trong phần tính == đúng con số đang hiện ở tiêu đề.
        foreach (var c in report.Criteria)
        {
            Assert.Equal(mile.Improvement![c.Name], c.DeltaPct);
            Assert.Equal(c.DeltaPct, c.HeadlineDeltaPct);
            Assert.Equal(
                Math.Round(c.CurrentSessions.Average(s => s.Percentage), 2),
                c.CurrentAveragePercentage);
        }
        Assert.Equal(25m, Crit(report, "Clarity").DeltaPct);    // (60+70)/2 − 40
        Assert.Equal(25m, Crit(report, "Depth").DeltaPct);      // (50+60)/2 − 30
    }

    // ══ MỐC SO ═════════════════════════════════════════════════════════════════════════════
    // Chặng 1 so BASELINE (không phải chặng trước — nó không có chặng trước).
    [Fact]
    public async Task Chang1_SoVoiBaseline_KhongCoBuoiMoc()
    {
        using var t = new TestDb();
        var user = Guid.NewGuid();
        var s1 = AddScoredSessionAt(t, user, new DateTime(2026, 8, 1, 0, 0, 0, DateTimeKind.Utc), ("Clarity", 60m));

        var r = NewRoadmap(user, new Dictionary<string, decimal> { ["Clarity"] = 40m });
        var m1 = AddMilestone(r, 1, MilestoneStatus.InProgress);
        AddLesson(m1, 1, LessonStatus.Done, s1);
        t.Db.Roadmaps.Add(r);
        await t.Db.SaveChangesAsync();

        var report = await Svc(t).GetMilestoneScoreReportAsync(user, r.Id, m1.Id);

        Assert.NotNull(report);
        Assert.Equal(MilestoneScoreReference.Baseline, report!.ComparedWith);
        Assert.Null(report.ComparedWithTitle);
        var c = Crit(report, "Clarity");
        Assert.Equal(40m, c.ReferenceAveragePercentage);
        Assert.Equal(20m, c.DeltaPct);
        // baseline là snapshot SỐ đo lúc lập lộ trình — không có buổi nào đứng sau nó.
        Assert.Empty(c.ReferenceSessions);
    }

    // Chặng trước KHÔNG có buổi nào → rơi về baseline (giữ nguyên luật cũ của improvement).
    [Fact]
    public async Task ChangTruocRong_RoiVeBaseline()
    {
        using var t = new TestDb();
        var user = Guid.NewGuid();
        var s2 = AddScoredSessionAt(t, user, new DateTime(2026, 8, 3, 0, 0, 0, DateTimeKind.Utc), ("Clarity", 90m));

        var r = NewRoadmap(user, new Dictionary<string, decimal> { ["Clarity"] = 40m });
        var m1 = AddMilestone(r, 1, MilestoneStatus.Pending);
        AddLesson(m1, 1, LessonStatus.Theory, null);          // chặng 1 chưa luyện buổi nào
        var m2 = AddMilestone(r, 2, MilestoneStatus.InProgress);
        AddLesson(m2, 1, LessonStatus.Done, s2);
        t.Db.Roadmaps.Add(r);
        await t.Db.SaveChangesAsync();

        var report = await Svc(t).GetMilestoneScoreReportAsync(user, r.Id, m2.Id);

        Assert.NotNull(report);
        Assert.Equal(MilestoneScoreReference.Baseline, report!.ComparedWith);
        Assert.Null(report.ComparedWithTitle);
        Assert.Equal(50m, Crit(report, "Clarity").DeltaPct);   // 90 − 40
        Assert.Empty(Crit(report, "Clarity").ReferenceSessions);
    }

    // Tiêu chí KHÔNG có mốc → deltaPct null, KHÔNG phải 0. 0 nghĩa là "không tiến bộ";
    // null nghĩa là "chưa có gì để so" — nói nhầm là nói sai với người học (BK23).
    [Fact]
    public async Task TieuChiKhongCoMoc_DeltaNull_KhongPhaiZero()
    {
        using var t = new TestDb();
        var user = Guid.NewGuid();
        // baseline chỉ có Clarity; Depth hoàn toàn không có mốc.
        var s1 = AddScoredSessionAt(t, user, new DateTime(2026, 8, 1, 0, 0, 0, DateTimeKind.Utc), ("Clarity", 60m), ("Depth", 55m));

        var r = NewRoadmap(user, new Dictionary<string, decimal> { ["Clarity"] = 40m });
        var m1 = AddMilestone(r, 1, MilestoneStatus.InProgress);
        AddLesson(m1, 1, LessonStatus.Done, s1);
        t.Db.Roadmaps.Add(r);
        await t.Db.SaveChangesAsync();

        var report = await Svc(t).GetMilestoneScoreReportAsync(user, r.Id, m1.Id);

        Assert.NotNull(report);
        var depth = Crit(report!, "Depth");
        Assert.Null(depth.DeltaPct);
        Assert.Null(depth.ReferenceAveragePercentage);
        Assert.NotEqual(0m, depth.DeltaPct);
        // …nhưng ĐIỂM của chặng vẫn hiện đầy đủ: báo cáo có ích kể cả khi không có delta.
        Assert.Equal(55m, depth.CurrentAveragePercentage);
        Assert.Single(depth.CurrentSessions);
    }

    // Không có mốc nào cả (baseline null, không chặng trước) → comparedWith "none", vẫn xem được điểm.
    [Fact]
    public async Task KhongCoMocNao_ComparedWithNone_VanXemDuocDiem()
    {
        using var t = new TestDb();
        var user = Guid.NewGuid();
        var s1 = AddScoredSessionAt(t, user, new DateTime(2026, 8, 1, 0, 0, 0, DateTimeKind.Utc), ("Clarity", 60m));

        var r = NewRoadmap(user, null);
        var m1 = AddMilestone(r, 1, MilestoneStatus.InProgress);
        AddLesson(m1, 1, LessonStatus.Done, s1);
        t.Db.Roadmaps.Add(r);
        await t.Db.SaveChangesAsync();

        var report = await Svc(t).GetMilestoneScoreReportAsync(user, r.Id, m1.Id);

        Assert.NotNull(report);
        Assert.Equal(MilestoneScoreReference.None, report!.ComparedWith);
        Assert.Equal(60m, Crit(report, "Clarity").CurrentAveragePercentage);
        Assert.Null(Crit(report, "Clarity").DeltaPct);
    }

    // ══ NGUỒN SỐ ═══════════════════════════════════════════════════════════════════════════
    // Chặng CHƯA hoàn thành vẫn xem được (tính lúc đọc) — không gác sau Completed.
    [Fact]
    public async Task ChangChuaHoanThanh_VanTraDuoc_Source_Computed()
    {
        using var t = new TestDb();
        var user = Guid.NewGuid();
        var s1 = AddScoredSessionAt(t, user, new DateTime(2026, 8, 1, 0, 0, 0, DateTimeKind.Utc), ("Clarity", 60m));

        var r = NewRoadmap(user, new Dictionary<string, decimal> { ["Clarity"] = 40m });
        var m1 = AddMilestone(r, 1, MilestoneStatus.InProgress);
        AddLesson(m1, 1, LessonStatus.Done, s1);
        AddLesson(m1, 2, LessonStatus.Practicing, null);       // còn bài dở ⇒ chặng chưa xong
        t.Db.Roadmaps.Add(r);
        await t.Db.SaveChangesAsync();

        var report = await Svc(t).GetMilestoneScoreReportAsync(user, r.Id, m1.Id);

        Assert.NotNull(report);
        Assert.Equal(MilestoneScoreSource.Computed, report!.Source);
        Assert.Equal("InProgress", report.MilestoneStatus);
        Assert.Equal(20m, Crit(report, "Clarity").DeltaPct);
        // Chưa chốt sổ ⇒ chưa có con số nào trên tiêu đề để đối chiếu.
        Assert.Null(Crit(report, "Clarity").HeadlineDeltaPct);
    }

    // Chặng Completed TRƯỚC bản này (không có snapshot) → tính lại, nhưng phải NÓI RA bằng
    // `recomputed` + trả kèm con số đã chốt để mọi sai lệch nhìn thấy được, không âm thầm.
    [Fact]
    public async Task ChangCompletedTruocBanNay_Source_Recomputed_VaLoHeadlineDeLech()
    {
        using var t = new TestDb();
        var user = Guid.NewGuid();
        var s1 = AddScoredSessionAt(t, user, new DateTime(2026, 8, 1, 0, 0, 0, DateTimeKind.Utc), ("Clarity", 60m));

        var r = NewRoadmap(user, new Dictionary<string, decimal> { ["Clarity"] = 40m });
        var m1 = AddMilestone(r, 1, MilestoneStatus.Completed);
        // improvement đã chốt từ trước (giá trị CŨ), snapshot thì không có.
        m1.Improvement = new Dictionary<string, decimal> { ["Clarity"] = 99m };
        m1.ScoreSnapshot = null;
        AddLesson(m1, 1, LessonStatus.Done, s1);
        t.Db.Roadmaps.Add(r);
        await t.Db.SaveChangesAsync();

        var report = await Svc(t).GetMilestoneScoreReportAsync(user, r.Id, m1.Id);

        Assert.NotNull(report);
        Assert.Equal(MilestoneScoreSource.Recomputed, report!.Source);
        var c = Crit(report, "Clarity");
        Assert.Equal(20m, c.DeltaPct);            // tính lại từ dữ liệu hiện tại
        Assert.Equal(99m, c.HeadlineDeltaPct);    // con số đang hiện ở tiêu đề
        Assert.NotEqual(c.DeltaPct, c.HeadlineDeltaPct);   // lệch — và client NHÌN THẤY được
    }

    // ══ LUYỆN LẠI ══════════════════════════════════════════════════════════════════════════
    // Bài đã luyện lại: điểm chặng đếm buổi MỚI NHẤT (lesson.session_id), và buổi liệt kê mang
    // `attemptNo` để người học hiểu "Lần 2" chứ không tưởng hệ thống làm mất một buổi.
    [Fact]
    public async Task BaiLuyenLai_DemBuoiMoiNhat_VaLoAttemptNo()
    {
        using var t = new TestDb();
        var user = Guid.NewGuid();
        var lan1 = AddScoredSessionAt(t, user, new DateTime(2026, 8, 1, 0, 0, 0, DateTimeKind.Utc), ("Clarity", 40m));
        var lan2 = AddScoredSessionAt(t, user, new DateTime(2026, 8, 5, 0, 0, 0, DateTimeKind.Utc), ("Clarity", 80m));

        var r = NewRoadmap(user, null);
        var m1 = AddMilestone(r, 1, MilestoneStatus.InProgress);
        var lesson = AddLesson(m1, 1, LessonStatus.Done, lan2, "Bài A");   // session_id = buổi MỚI NHẤT
        t.Db.Roadmaps.Add(r);
        await t.Db.SaveChangesAsync();

        t.Db.RoadmapLessonAttempts.AddRange(
            new RoadmapLessonAttempt { LessonId = lesson.Id, SessionId = lan1, AttemptNo = 1 },
            new RoadmapLessonAttempt { LessonId = lesson.Id, SessionId = lan2, AttemptNo = 2 });
        await t.Db.SaveChangesAsync();

        var report = await Svc(t).GetMilestoneScoreReportAsync(user, r.Id, m1.Id);

        Assert.NotNull(report);
        var c = Crit(report!, "Clarity");
        var s = Assert.Single(c.CurrentSessions);
        Assert.Equal(lan2, s.SessionId);
        Assert.Equal(2, s.AttemptNo);
        Assert.Equal("Bài A", s.LessonTitle);
        // Bất biến gốc vẫn đứng: trung bình danh sách == con số chặng.
        Assert.Equal(80m, c.CurrentAveragePercentage);
        Assert.Equal(Math.Round(c.CurrentSessions.Average(x => x.Percentage), 2), c.CurrentAveragePercentage);
    }

    // Buổi không có dòng lần-làm nào (dữ liệu trước khi có bảng attempts) → attemptNo null,
    // KHÔNG bịa thành 1.
    [Fact]
    public async Task BuoiKhongCoDongLanLam_AttemptNoNull()
    {
        using var t = new TestDb();
        var user = Guid.NewGuid();
        var s1 = AddScoredSessionAt(t, user, new DateTime(2026, 8, 1, 0, 0, 0, DateTimeKind.Utc), ("Clarity", 60m));

        var r = NewRoadmap(user, null);
        var m1 = AddMilestone(r, 1, MilestoneStatus.InProgress);
        AddLesson(m1, 1, LessonStatus.Done, s1);
        t.Db.Roadmaps.Add(r);
        await t.Db.SaveChangesAsync();

        var report = await Svc(t).GetMilestoneScoreReportAsync(user, r.Id, m1.Id);

        Assert.Null(Assert.Single(Crit(report!, "Clarity").CurrentSessions).AttemptNo);
    }

    // Nhiều bài trong chặng ⇒ nhiều buổi, KHÔNG gộp: liệt kê đủ, xếp theo mốc chấm.
    [Fact]
    public async Task NhieuBai_LietKeDuMoiBuoi_XepTheoMocCham()
    {
        using var t = new TestDb();
        var user = Guid.NewGuid();
        var sMuon = AddScoredSessionAt(t, user, new DateTime(2026, 8, 9, 0, 0, 0, DateTimeKind.Utc), ("Clarity", 90m));
        var sSom = AddScoredSessionAt(t, user, new DateTime(2026, 8, 2, 0, 0, 0, DateTimeKind.Utc), ("Clarity", 30m));

        var r = NewRoadmap(user, null);
        var m1 = AddMilestone(r, 1, MilestoneStatus.InProgress);
        AddLesson(m1, 1, LessonStatus.Done, sMuon, "Bài muộn");
        AddLesson(m1, 2, LessonStatus.Done, sSom, "Bài sớm");
        t.Db.Roadmaps.Add(r);
        await t.Db.SaveChangesAsync();

        var report = await Svc(t).GetMilestoneScoreReportAsync(user, r.Id, m1.Id);

        var c = Crit(report!, "Clarity");
        Assert.Equal(2, c.CurrentSessions.Count);
        Assert.Equal(["Bài sớm", "Bài muộn"], c.CurrentSessions.Select(x => x.LessonTitle));
        Assert.Equal(60m, c.CurrentAveragePercentage);   // (30+90)/2
        Assert.Equal(Math.Round(c.CurrentSessions.Average(x => x.Percentage), 2), c.CurrentAveragePercentage);
    }

    // Một buổi có HAI dòng cùng TÊN tiêu chí (rubric đổi version ⇒ hai criterion_id khác nhau,
    // tên trùng — UNIQUE là (session_id, criterion_id) nên trạng thái này dựng được thật).
    //
    // Đây là chỗ DUY NHẤT mà "trung bình trên dòng điểm" khác "trung bình trên buổi", nên cũng là
    // chỗ duy nhất chứng minh được phần tính hiển thị và con số chốt đi chung MỘT đường: liệt kê
    // đủ cả hai dòng thì trung bình danh sách == con số chặng; gộp lại thì lệch.
    [Fact]
    public async Task MotBuoiHaiDongCungTenTieuChi_LietKeDu_TrungBinhVanKhop()
    {
        using var t = new TestDb();
        var user = Guid.NewGuid();
        var at = new DateTime(2026, 8, 1, 0, 0, 0, DateTimeKind.Utc);

        var session = TestDb.Session(user, SessionStatus.Scored, createdAt: at);
        t.Db.PracticeSessions.Add(session);
        // Hai bản rubric khác version, TRÙNG TÊN.
        var v1 = TestDb.Criterion(JobCategory.BE, version: 1, active: false, name: "Clarity");
        var v2 = TestDb.Criterion(JobCategory.BE, version: 2, name: "Clarity");
        t.Db.RubricCriteria.AddRange(v1, v2);
        foreach (var (crit, pct) in new[] { (v1, 20m), (v2, 80m) })
            t.Db.SessionCriterionScores.Add(new SessionCriterionScore
            {
                Id = Guid.NewGuid(),
                SessionId = session.Id,
                CriterionId = crit.Id,
                CriterionName = "Clarity",
                AverageScore = Math.Round(pct / 20m, 2),
                MaxScore = 5,
                Percentage = pct,
                Weight = 1m,
                NeedsImprovement = pct < 50m,
                CreatedAt = at
            });
        await t.Db.SaveChangesAsync();

        var r = NewRoadmap(user, new Dictionary<string, decimal> { ["Clarity"] = 40m });
        var m1 = AddMilestone(r, 1, MilestoneStatus.InProgress);
        AddLesson(m1, 1, LessonStatus.Done, session.Id);
        t.Db.Roadmaps.Add(r);
        await t.Db.SaveChangesAsync();

        var report = await Svc(t).GetMilestoneScoreReportAsync(user, r.Id, m1.Id);

        var c = Crit(report!, "Clarity");
        Assert.Equal(2, c.CurrentSessions.Count);                    // KHÔNG gộp
        Assert.Equal(50m, c.CurrentAveragePercentage);               // (20+80)/2 — trên DÒNG điểm
        Assert.Equal(Math.Round(c.CurrentSessions.Average(x => x.Percentage), 2), c.CurrentAveragePercentage);
        Assert.Equal(10m, c.DeltaPct);                               // 50 − 40, cộng ra từ chính hai dòng trên
    }

    // Tiếp ca trên, nhưng SỐ DÒNG LỆCH NHAU giữa các buổi (buổi A 2 dòng, buổi B 1 dòng) —
    // rubric đổi version giữa chừng chặng là dựng được thật.
    //
    // Chỉ ở đây "trung bình trên dòng" mới TÁCH KHỎI "trung bình trên buổi" (33.33 vs 25). Ca cân
    // bằng ở test trên không phân biệt được hai phép tính, nên nếu ai đó cho `deltaPct` đi một
    // đường tính riêng thì không test nào đỏ — mà `deltaPct` chính là con số lên tiêu đề.
    [Fact]
    public async Task SoDongLechNhauGiuaCacBuoi_DeltaVanBamConSoHienThi()
    {
        using var t = new TestDb();
        var user = Guid.NewGuid();
        var at = new DateTime(2026, 8, 1, 0, 0, 0, DateTimeKind.Utc);

        var v1 = TestDb.Criterion(JobCategory.BE, version: 1, active: false, name: "Clarity");
        var v2 = TestDb.Criterion(JobCategory.BE, version: 2, name: "Clarity");
        t.Db.RubricCriteria.AddRange(v1, v2);

        Guid AddSession(params (RubricCriterion crit, decimal pct)[] rows)
        {
            var session = TestDb.Session(user, SessionStatus.Scored, createdAt: at);
            t.Db.PracticeSessions.Add(session);
            foreach (var (crit, pct) in rows)
                t.Db.SessionCriterionScores.Add(new SessionCriterionScore
                {
                    Id = Guid.NewGuid(),
                    SessionId = session.Id,
                    CriterionId = crit.Id,
                    CriterionName = "Clarity",
                    AverageScore = Math.Round(pct / 20m, 2),
                    MaxScore = 5,
                    Percentage = pct,
                    Weight = 1m,
                    NeedsImprovement = pct < 50m,
                    CreatedAt = at
                });
            return session.Id;
        }

        var sA = AddSession((v1, 20m), (v2, 80m));   // 2 dòng
        var sB = AddSession((v2, 0m));               // 1 dòng
        await t.Db.SaveChangesAsync();

        var r = NewRoadmap(user, new Dictionary<string, decimal> { ["Clarity"] = 13.33m });
        var m1 = AddMilestone(r, 1, MilestoneStatus.InProgress);
        AddLesson(m1, 1, LessonStatus.Done, sA);
        AddLesson(m1, 2, LessonStatus.Done, sB);
        t.Db.Roadmaps.Add(r);
        await t.Db.SaveChangesAsync();

        var report = await Svc(t).GetMilestoneScoreReportAsync(user, r.Id, m1.Id);

        var c = Crit(report!, "Clarity");
        Assert.Equal(3, c.CurrentSessions.Count);
        // Trên DÒNG: (20+80+0)/3 = 33.33.  Trên BUỔI thì sẽ là (50+0)/2 = 25 — khác hẳn.
        Assert.Equal(33.33m, c.CurrentAveragePercentage);
        Assert.Equal(Math.Round(c.CurrentSessions.Average(x => x.Percentage), 2), c.CurrentAveragePercentage);
        // delta phải bám ĐÚNG con số đang hiển thị, không phải một phép tính thứ hai.
        Assert.Equal(c.CurrentAveragePercentage - c.ReferenceAveragePercentage, c.DeltaPct);
        Assert.Equal(20m, c.DeltaPct);
    }

    // ══ QUYỀN SỞ HỮU / 404 ═════════════════════════════════════════════════════════════════
    [Fact]
    public async Task ChangLa_404_LoTrinhLa_404_NguoiKhac_403()
    {
        using var t = new TestDb();
        var owner = Guid.NewGuid();
        var stranger = Guid.NewGuid();

        var r = NewRoadmap(owner, null);
        var m1 = AddMilestone(r, 1, MilestoneStatus.Pending);
        t.Db.Roadmaps.Add(r);
        await t.Db.SaveChangesAsync();

        var svc = Svc(t);

        // chặng không thuộc lộ trình → 404
        Assert.IsType<NotFoundObjectResult>(
            await Controller(svc, owner).GetMilestoneScoreReport(r.Id, Guid.NewGuid(), default));

        // lộ trình không tồn tại → 404
        Assert.IsType<NotFoundObjectResult>(
            await Controller(svc, owner).GetMilestoneScoreReport(Guid.NewGuid(), m1.Id, default));

        // lộ trình của người khác → 403 (không phải 404: hai thứ mang nghĩa khác nhau)
        var forbidden = Assert.IsType<ObjectResult>(
            await Controller(svc, stranger).GetMilestoneScoreReport(r.Id, m1.Id, default));
        Assert.Equal(StatusCodes.Status403Forbidden, forbidden.StatusCode);

        // chủ sở hữu → 200
        Assert.IsType<OkObjectResult>(
            await Controller(svc, owner).GetMilestoneScoreReport(r.Id, m1.Id, default));
    }
}
