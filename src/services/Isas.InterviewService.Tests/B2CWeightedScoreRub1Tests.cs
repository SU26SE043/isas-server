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

namespace Isas.InterviewService.Tests;

/// <summary>
/// RUB1 · hợp đồng D — buổi B2C MỚI tính điểm CÓ TRỌNG SỐ (INT-10); buổi cũ giữ trung bình cộng.
///
/// <para>Bộ dữ liệu chọn sao cho BA cách tính cho ra BA con số khác nhau — nếu không, test không phân
/// biệt được cách nào đang chạy: tiêu chí A (w 0.5) 80%, B (w 0.2) 40%, C (w 0.3) KHÔNG được hỏi.</para>
/// <list type="bullet">
/// <item>Weighted đúng (chia Σw tiêu chí CÓ điểm): (80×0.5 + 40×0.2) / 0.7 = <b>68.57</b></item>
/// <item>Chia Σw CẢ BỘ (sai — phạt ngầm tiêu chí không được hỏi như tính 0): 48 / 1.0 = <b>48</b></item>
/// <item>Trung bình cộng (luật cũ): (80 + 40) / 2 = <b>60</b></item>
/// </list>
/// </summary>
public class B2CWeightedScoreRub1Tests
{
    private const decimal WeightedExpected = 68.57m;
    private const decimal AverageExpected = 60m;

    // ── Đường TẠO buổi ghim công thức ─────────────────────────────────────────────────────────

    [Fact]
    public async Task TaoBuoiB2C_GhimWeighted()
    {
        using var t = new TestDb();
        var res = await BuildPractice(t).CreateSessionAsync(
            Guid.NewGuid(), new CreatePracticeSessionRequest(null, null, JobCategory.BE));

        var s = await t.Db.PracticeSessions.AsNoTracking().FirstAsync(x => x.Id == res.Id);
        Assert.Null(s.CampaignId);
        Assert.Equal(B2CScoreFormula.Weighted, s.B2CScoreFormula);
    }

    // Lesson (BC14) đi CÙNG đường CreateSessionInternalAsync — khoá riêng để ai tách nhánh lesson ra
    // sau này không vô tình bỏ rơi con dấu.
    [Fact]
    public async Task TaoBuoiLesson_GhimWeighted()
    {
        using var t = new TestDb();
        var sessionId = Guid.NewGuid();
        await BuildPractice(t).CreateLessonSessionAsync(
            Guid.NewGuid(), new CreatePracticeSessionRequest(null, null, JobCategory.BE),
            sessionId, focusCriteria: null, lessonContext: null);

        var s = await t.Db.PracticeSessions.AsNoTracking().FirstAsync(x => x.Id == sessionId);
        Assert.Equal(B2CScoreFormula.Weighted, s.B2CScoreFormula);
    }

    // B2B KHÔNG đọc cột này (điểm tổng B2B tính ở SessionScoringNotifier/Campaign) ⇒ phải để null,
    // không được ghi một con dấu nói dối về một công thức không dùng.
    [Fact]
    public async Task TaoBuoiB2B_KhongGhimCongThuc()
    {
        using var t = new TestDb();
        var res = await BuildCampaignPractice(t).CreateCampaignSessionAsync(
            Guid.NewGuid(),
            new CreateCampaignSessionRequest(
                Guid.NewGuid(), Guid.NewGuid(), JobCategory.BE, new[] { "Q1" },
                new[] { new CampaignCriterionInput("Communication", null, 1.0m, 5) }));

        await using var read = t.NewContext();
        var s = await read.PracticeSessions.AsNoTracking().SingleAsync(x => x.Id == res.Id);
        Assert.Null(s.B2CScoreFormula);
    }

    // ── Đường TÍNH điểm ───────────────────────────────────────────────────────────────────────

    [Fact]
    public async Task Weighted_TieuChiKhongDuocHoi_RoiKhoiCaTuLanMau()
    {
        using var t = new TestDb();
        var sessionId = SeedSession(t, B2CScoreFormula.Weighted, skipPenalty: false);

        await TestDb.ResultService(t.Db).ComputeAndStoreAsync(sessionId);

        Assert.Equal(WeightedExpected, await OverallAsync(t, sessionId));
        // Tiêu chí không được hỏi KHÔNG có dòng breakdown (không ghi 0.00).
        Assert.Equal(2, await t.Db.SessionCriterionScores.CountAsync(x => x.SessionId == sessionId));
    }

    /// <summary>Buổi trước RUB1 (null) ⇒ ĐÚNG con số cũ — chấm lại (republisher) không đổi điểm.</summary>
    [Fact]
    public async Task FormulaNull_GiuTrungBinhCong_KhongHoiTo()
    {
        using var t = new TestDb();
        var sessionId = SeedSession(t, formula: null, skipPenalty: false);

        await TestDb.ResultService(t.Db).ComputeAndStoreAsync(sessionId);

        Assert.Equal(AverageExpected, await OverallAsync(t, sessionId));
    }

    [Fact]
    public async Task FormulaAverageTuongMinh_TrungBinhCong()
    {
        using var t = new TestDb();
        var sessionId = SeedSession(t, B2CScoreFormula.Average, skipPenalty: false);

        await TestDb.ResultService(t.Db).ComputeAndStoreAsync(sessionId);

        Assert.Equal(AverageExpected, await OverallAsync(t, sessionId));
    }

    /// <summary>Gộp có trọng số TRƯỚC, nhân phạt bỏ câu SAU (CAMP-21): 68.57 × 2/3 = 45.71.</summary>
    [Fact]
    public async Task Weighted_RoiNhanPhatBoCauGoc()
    {
        using var t = new TestDb();
        var sessionId = SeedSession(t, B2CScoreFormula.Weighted, skipPenalty: true, emptySeeds: 1);

        await TestDb.ResultService(t.Db).ComputeAndStoreAsync(sessionId);

        Assert.Equal(45.71m, await OverallAsync(t, sessionId));
    }

    /// <summary>
    /// Trọng số lấy từ bộ ĐÃ GHIM (v1, đã hạ cờ), KHÔNG từ bộ đang hiệu lực (v2 đảo trọng số). Admin đổi
    /// trọng số giữa buổi không được đổi điểm của buổi đã bắt đầu.
    /// </summary>
    [Fact]
    public async Task Weighted_DungTrongSoCuaBoDaGhim()
    {
        using var t = new TestDb();
        var sessionId = SeedSession(t, B2CScoreFormula.Weighted, skipPenalty: false, pinV1WithActiveV2: true);

        await TestDb.ResultService(t.Db).ComputeAndStoreAsync(sessionId);

        Assert.Equal(WeightedExpected, await OverallAsync(t, sessionId));
    }

    // ── Đường GET kết quả ─────────────────────────────────────────────────────────────────────

    [Fact]
    public async Task Get_Weighted_DongGop_CongLai_KhopDiemTruocPhat_VaLietKeTieuChiKhongCham()
    {
        using var t = new TestDb();
        var candidate = Guid.NewGuid();
        var sessionId = SeedSession(t, B2CScoreFormula.Weighted, skipPenalty: true,
            pinV1WithActiveV2: true, candidateId: candidate);
        await TestDb.ResultService(t.Db).ComputeAndStoreAsync(sessionId);

        var r = (await BuildPracticeForGet(t).GetSessionAsync(candidate, sessionId))!.Result!;

        Assert.Equal("Weighted", r.ScoreFormula);
        Assert.Equal(WeightedExpected, r.ScoreBeforePenalty);
        Assert.Equal(WeightedExpected, r.OverallScore);   // đủ câu gốc ⇒ không phạt

        var a = r.CriteriaScores.Single(c => c.Name == "A");
        var b = r.CriteriaScores.Single(c => c.Name == "B");
        Assert.Equal(0.7143m, a.EffectiveWeight);
        Assert.Equal(57.14m, a.Contribution);
        Assert.Equal(0.2857m, b.EffectiveWeight);
        Assert.Equal(11.43m, b.Contribution);
        // Bất biến hợp đồng: Σ contribution ≈ scoreBeforePenalty (lệch do làm tròn ≤ 0.05).
        var sum = r.CriteriaScores.Sum(c => c.Contribution!.Value);
        Assert.True(Math.Abs(sum - r.ScoreBeforePenalty!.Value) <= 0.05m, $"Σ={sum} vs {r.ScoreBeforePenalty}");

        // Tiêu chí không được chấm lấy từ bộ ĐÃ GHIM (v1: chỉ C) — KHÔNG lẫn "D" chỉ có ở bộ v2 đang
        // hiệu lực. Liệt kê theo bộ hôm nay là nói với người luyện rằng họ "không được chấm" một tiêu
        // chí chưa từng có trong thước đo của buổi họ.
        var unassessed = Assert.Single(r.UnassessedCriteria!);
        Assert.Equal("C", unassessed.Name);
        Assert.Equal(0.3m, unassessed.Weight);
    }

    /// <summary>Buổi trước RUB1 ⇒ mọi field mới null — không vẽ null thành "Average"/"Weighted" (BK23).</summary>
    [Fact]
    public async Task Get_BuoiCu_MoiFieldMoiNull()
    {
        using var t = new TestDb();
        var candidate = Guid.NewGuid();
        var sessionId = SeedSession(t, formula: null, skipPenalty: true, candidateId: candidate);
        await TestDb.ResultService(t.Db).ComputeAndStoreAsync(sessionId);

        var r = (await BuildPracticeForGet(t).GetSessionAsync(candidate, sessionId))!.Result!;

        Assert.Null(r.ScoreFormula);
        Assert.Null(r.UnassessedCriteria);
        Assert.All(r.CriteriaScores, c =>
        {
            Assert.Null(c.EffectiveWeight);
            Assert.Null(c.Contribution);
        });
        Assert.Equal(AverageExpected, r.ScoreBeforePenalty);
        Assert.Equal(AverageExpected, r.OverallScore);
    }

    // ── Báo cáo lộ trình dùng CÙNG công thức ──────────────────────────────────────────────────

    /// <summary>
    /// Đường xu hướng của báo cáo lộ trình gộp điểm từng buổi bằng CÙNG hàm với màn kết quả. Hai buổi
    /// cùng dữ liệu, khác con dấu ⇒ 68.57 (Weighted) và 60 (buổi cũ). Trước RUB1 nó tự tính trung bình
    /// cộng riêng ⇒ cùng một buổi Weighted, màn kết quả nói 68.57 còn màn lộ trình nói 60.
    /// </summary>
    [Fact]
    public async Task BaoCaoLoTrinh_DiemTungBuoi_TheoConDauCuaBuoi()
    {
        using var t = new TestDb();
        var user = Guid.NewGuid();
        var t0 = new DateTime(2026, 10, 1, 8, 0, 0, DateTimeKind.Utc);
        var weighted = SeedSession(t, B2CScoreFormula.Weighted, skipPenalty: false, candidateId: user);
        await TestDb.ResultService(t.Db).ComputeAndStoreAsync(weighted);
        var legacy = SeedSession(t, formula: null, skipPenalty: false, candidateId: user, reuseCriteria: true);
        await TestDb.ResultService(t.Db).ComputeAndStoreAsync(legacy);
        await PinScoredAtAsync(t, weighted, t0);
        await PinScoredAtAsync(t, legacy, t0.AddDays(1));

        var roadmap = new Roadmap
        {
            Id = Guid.NewGuid(), CandidateId = user, JobCategory = JobCategory.BE,
            Level = RoadmapLevel.Junior, Status = RoadmapStatus.Active, CreatedAt = t0
        };
        var milestone = new RoadmapMilestone
        {
            Id = Guid.NewGuid(), OrderNo = 1, Title = "M1", FocusCriteria = ["A", "B"],
            Status = MilestoneStatus.InProgress
        };
        milestone.Lessons.Add(new RoadmapLesson
            { Id = Guid.NewGuid(), OrderNo = 1, Title = "L1", Status = LessonStatus.Done, SessionId = weighted });
        milestone.Lessons.Add(new RoadmapLesson
            { Id = Guid.NewGuid(), OrderNo = 2, Title = "L2", Status = LessonStatus.Done, SessionId = legacy });
        roadmap.Milestones.Add(milestone);
        t.Db.Roadmaps.Add(roadmap);
        await t.Db.SaveChangesAsync();

        var gen = new Mock<IAiServiceRoadmapGenerator>();
        gen.Setup(g => g.SummarizeRoadmapAsync(
                It.IsAny<string>(), It.IsAny<string>(),
                It.IsAny<IReadOnlyList<RoadmapCriteriaProgress>>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(new RoadmapSummaryAiResult([], [], [], null));
        var svc = new RoadmapReportService(
            t.Db, gen.Object, TestDb.Thresholds(t.Db), NullLogger<RoadmapReportService>.Instance);

        var report = (await svc.GetReportAsync(user, roadmap.Id))!;

        Assert.Equal(2, report.Progress.Count);
        Assert.Equal(WeightedExpected, report.Progress[0].OverallPercentage);
        Assert.Equal(AverageExpected, report.Progress[1].OverallPercentage);
    }

    // ── helpers ───────────────────────────────────────────────────────────────────────────────

    /// <summary>
    /// Buổi B2C đã Scored: tiêu chí A (w 0.5) chấm 4/5 = 80%, B (w 0.2) chấm 2/5 = 40%, C (w 0.3) không
    /// câu nào chấm. Hai câu gốc đều trả lời (+ <paramref name="emptySeeds"/> câu gốc bỏ trống).
    /// <paramref name="pinV1WithActiveV2"/>: buổi ghim bộ chuẩn v1 (A/B/C — ĐÃ HẠ CỜ), trong khi bộ đang
    /// hiệu lực là v2 với trọng số đảo và thêm tiêu chí D.
    /// </summary>
    private static Guid SeedSession(
        TestDb t, B2CScoreFormula? formula, bool skipPenalty, int emptySeeds = 0,
        bool pinV1WithActiveV2 = false, Guid? candidateId = null, bool reuseCriteria = false)
    {
        var session = TestDb.Session(candidateId ?? Guid.NewGuid(), SessionStatus.Scored, JobCategory.BE);
        session.B2CScoreFormula = formula;
        session.SkipPenalty = skipPenalty;

        RubricCriterion a, b;
        if (reuseCriteria)
        {
            a = t.Db.RubricCriteria.Single(c => c.Name == "A" && c.IsActive);
            b = t.Db.RubricCriteria.Single(c => c.Name == "B" && c.IsActive);
        }
        else
        {
            var v1Active = !pinV1WithActiveV2;
            a = Crit("A", 0.5m, version: 1, active: v1Active);
            b = Crit("B", 0.2m, version: 1, active: v1Active);
            var c = Crit("C", 0.3m, version: 1, active: v1Active);
            t.Db.RubricCriteria.AddRange(a, b, c);
            if (pinV1WithActiveV2)
            {
                session.B2CRubricVersion = 1;
                session.B2CRubricOwnerId = null;
                t.Db.RubricCriteria.AddRange(
                    Crit("A", 0.1m, version: 2, active: true),
                    Crit("B", 0.4m, version: 2, active: true),
                    Crit("C", 0.2m, version: 2, active: true),
                    Crit("D", 0.3m, version: 2, active: true));
            }
        }

        var q1 = TestDb.Question(session.Id, 1);
        var q2 = TestDb.Question(session.Id, 2);
        var a1 = TestDb.Answer(session.Id, q1.Id, AnswerStatus.Scored, DateTime.UtcNow, DateTime.UtcNow);
        var a2 = TestDb.Answer(session.Id, q2.Id, AnswerStatus.Scored, DateTime.UtcNow, DateTime.UtcNow);
        t.Db.AddRange(session, q1, q2, a1, a2,
            Score(a1.Id, a.Id, 4m), Score(a2.Id, a.Id, 4m),
            Score(a1.Id, b.Id, 2m), Score(a2.Id, b.Id, 2m));
        for (var i = 0; i < emptySeeds; i++)
            t.Db.Add(TestDb.Question(session.Id, 3 + i));   // câu gốc bỏ trống — không answer nào
        t.Db.SaveChanges();
        return session.Id;
    }

    private static async Task PinScoredAtAsync(TestDb t, Guid sessionId, DateTime at)
        => await t.Db.SessionCriterionScores.Where(x => x.SessionId == sessionId)
            .ExecuteUpdateAsync(u => u.SetProperty(x => x.CreatedAt, at));

    private static async Task<decimal?> OverallAsync(TestDb t, Guid sessionId)
        => (await t.Db.PracticeSessions.AsNoTracking().FirstAsync(x => x.Id == sessionId)).OverallScore;

    private static RubricCriterion Crit(string name, decimal weight, int version, bool active)
        => new()
        {
            Id = Guid.NewGuid(),
            Name = name,
            Description = name,
            Weight = weight,
            MaxScore = 5,
            IsActive = active,
            JobCategory = JobCategory.BE,
            CampaignId = null,
            CandidateId = null,
            Language = "vi",
            Version = version
        };

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

    private static PracticeService BuildPracticeForGet(TestDb t)
        => new(t.Db, new Mock<IStorageService>().Object,
            new Mock<IAiServiceQuestionGenerator>().Object, new Mock<ISessionScoringNotifier>().Object,
            new Mock<ICreditReservationClient>().Object, NullLogger<PracticeService>.Instance);

    private static PracticeService BuildPractice(TestDb t)
    {
        var gen = new Mock<IAiServiceQuestionGenerator>();
        gen.Setup(g => g.GenerateQuestionsAsync(
                It.IsAny<string>(), It.IsAny<string?>(), It.IsAny<string?>(),
                It.IsAny<string>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(new List<GeneratedQuestion> { new() { Content = "Q1" } });

        var reservation = new Mock<ICreditReservationClient>();
        reservation
            .Setup(r => r.ReserveAsync(It.IsAny<string>(), It.IsAny<Guid>(), It.IsAny<Guid>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(new CreditReservationResult(Guid.NewGuid(), 1));

        return new PracticeService(
            t.Db, new Mock<IStorageService>().Object, gen.Object,
            new Mock<ISessionScoringNotifier>().Object, reservation.Object,
            NullLogger<PracticeService>.Instance);
    }

    private static PracticeService BuildCampaignPractice(TestDb t)
    {
        var reservation = new Mock<ICreditReservationClient>();
        reservation
            .Setup(r => r.ReserveAsync(It.IsAny<string>(), It.IsAny<Guid>(), It.IsAny<Guid>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(new CreditReservationResult(Guid.NewGuid(), 1));

        return new PracticeService(t.Db, new Mock<IStorageService>().Object,
            new Mock<IAiServiceQuestionGenerator>().Object,
            new Mock<ISessionScoringNotifier>().Object,
            reservation.Object,
            NullLogger<PracticeService>.Instance,
            capacityOptions: Options.Create(new CapacityOptions { MaxConcurrentSessions = 0 }));
    }
}
