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

/// <summary>
/// BC15 — ngưỡng ĐẠT theo cấp độ do admin chỉnh, lưu bền vững, giá trị trong code chỉ còn là MẶC
/// ĐỊNH khi chưa ai chỉnh.
///
/// <para>Bất biến đắt nhất được khoá ở đây: <b>lộ trình đã đóng sổ KHÔNG bị ngưỡng mới sửa lại kết
/// luận</b>. Đổi được kết luận "Đạt/Chưa đạt" của một báo cáo đã phát cho người học là hỏng nghiệp
/// vụ chứ không phải bất tiện.</para>
/// </summary>
public class RoadmapLevelThresholdTests
{
    private static readonly JsonSerializerOptions Json = new(JsonSerializerDefaults.Web);

    private static AdminRoadmapThresholdController Controller(
        IRoadmapThresholdService svc, Guid? actor = null)
    {
        var claims = actor is Guid id
            ? new[] { new Claim(ClaimTypes.NameIdentifier, id.ToString()) }
            : [];
        return new AdminRoadmapThresholdController(svc)
        {
            ControllerContext = new ControllerContext
            {
                HttpContext = new DefaultHttpContext
                {
                    User = new ClaimsPrincipal(new ClaimsIdentity(claims, "test"))
                }
            }
        };
    }

    private static async Task<IReadOnlyList<RoadmapLevelThresholdResponse>> ListViaControllerAsync(
        IRoadmapThresholdService svc)
    {
        var result = await Controller(svc).ListAsync(default);
        return Assert.IsAssignableFrom<IReadOnlyList<RoadmapLevelThresholdResponse>>(
            Assert.IsType<OkObjectResult>(result.Result).Value);
    }

    // ── (1) GET trả ĐỦ BA thông tin: hiệu lực · mặc định · đã-chỉnh-chưa ───────────────
    // Chỉ trả giá trị hiệu lực thì "60" không phân biệt được "mặc định của code" với "ai đó đã đặt
    // đúng bằng mặc định" — admin không biết mình đang sửa cái gì so với cái gì.
    [Fact]
    public async Task Get_TraDuHieuLuc_MacDinh_VaCoDaChinhChua()
    {
        using var t = new TestDb();
        var svc = TestDb.Thresholds(t.Db);
        var admin = Guid.NewGuid();

        await svc.UpsertAsync(new Dictionary<string, int> { ["Junior"] = 75 }, admin);

        var all = await ListViaControllerAsync(svc);

        // Mọi cấp độ code biết đều xuất hiện, kể cả cấp chưa ai chỉnh.
        Assert.Equal(
            Enum.GetNames<RoadmapLevel>().OrderBy(x => x, StringComparer.Ordinal),
            all.Select(x => x.Level).OrderBy(x => x, StringComparer.Ordinal));

        var junior = all.Single(x => x.Level == "Junior");
        Assert.Equal(75, junior.EffectivePct);      // đang hiệu lực = giá trị admin đặt
        Assert.Equal(60, junior.DefaultPct);        // mặc định code — giá trị sau khi reset
        Assert.True(junior.IsOverridden);
        Assert.Equal(admin, junior.UpdatedBy);      // ghi vết ai sửa
        Assert.NotNull(junior.UpdatedAt);

        var senior = all.Single(x => x.Level == "Senior");
        Assert.Equal(80, senior.EffectivePct);
        Assert.Equal(80, senior.DefaultPct);
        Assert.False(senior.IsOverridden);          // phân biệt được với ca "đặt đúng bằng mặc định"
        Assert.Null(senior.UpdatedBy);
        Assert.Null(senior.UpdatedAt);
    }

    // ── (2) PUT ngoài dải [0,100] → 400, nêu rõ CẤP NÀO sai ───────────────────────────
    [Theory]
    [InlineData(-1)]
    [InlineData(101)]
    public async Task Put_NguongNgoaiDai_Tra400_NeuRoCapNao(int pct)
    {
        using var t = new TestDb();
        var svc = TestDb.Thresholds(t.Db);

        var res = await Controller(svc, Guid.NewGuid()).UpdateAsync(
            new UpdateRoadmapLevelThresholdsRequest
            {
                Thresholds = new Dictionary<string, int> { ["Middle"] = pct }
            }, default);

        var bad = Assert.IsType<BadRequestObjectResult>(res.Result);
        Assert.Contains("Middle", bad.Value!.ToString());
        Assert.Empty(await t.NewContext().RoadmapLevelThresholds.ToListAsync());
    }

    // ── (3) PUT rồi GET ra giá trị mới ─────────────────────────────────────────────────
    [Fact]
    public async Task Put_RoiGet_RaGiaTriMoi()
    {
        using var t = new TestDb();
        var svc = TestDb.Thresholds(t.Db);
        var ctrl = Controller(svc, Guid.NewGuid());

        var put = await ctrl.UpdateAsync(new UpdateRoadmapLevelThresholdsRequest
        {
            Thresholds = new Dictionary<string, int> { ["Fresher"] = 35, ["Senior"] = 95 }
        }, default);
        Assert.IsType<OkObjectResult>(put.Result);

        // Đọc lại bằng CONTEXT KHÁC: chứng minh đã bền vững xuống DB chứ không phải còn trong tracker.
        var fresh = TestDb.Thresholds(t.NewContext());
        var all = await ListViaControllerAsync(fresh);

        Assert.Equal(35, all.Single(x => x.Level == "Fresher").EffectivePct);
        Assert.Equal(95, all.Single(x => x.Level == "Senior").EffectivePct);
        // Cập nhật MỘT PHẦN: cấp không nêu trong body giữ nguyên.
        Assert.Equal(60, all.Single(x => x.Level == "Junior").EffectivePct);
        Assert.False(all.Single(x => x.Level == "Junior").IsOverridden);

        // Và đường ĐỌC thật (build report) cũng thấy giá trị mới.
        Assert.Equal(35, await fresh.ThresholdForAsync("Fresher"));
    }

    // ── (4) Cấp chưa chỉnh vẫn rơi về mặc định ────────────────────────────────────────
    [Theory]
    [InlineData("Fresher", 50)]
    [InlineData("Junior", 60)]
    [InlineData("Middle", 70)]
    [InlineData("Senior", 80)]
    public async Task CapChuaChinh_RoiVeMacDinh(string level, int expected)
    {
        using var t = new TestDb();
        Assert.Equal(expected, await TestDb.Thresholds(t.Db).ThresholdForAsync(level));
    }

    // ── (5) Mặc định vẫn tôn trọng cấu hình appsettings/env đang có ───────────────────
    // Không được biến lớp cấu hình cũ thành cột chết: deployment nào đang đặt
    // Roadmap__LevelThresholdPct__* phải tiếp tục có hiệu lực khi bảng chưa có hàng.
    [Fact]
    public async Task ChuaCoHangDb_VanTonTrongCauHinhAppsettings()
    {
        using var t = new TestDb();
        var opts = new RoadmapOptions
        {
            LevelThresholdPct = new Dictionary<string, int> { ["Junior"] = 66 }
        };
        var svc = TestDb.Thresholds(t.Db, opts);

        Assert.Equal(66, await svc.ThresholdForAsync("Junior"));
        var junior = (await svc.ListAsync()).Single(x => x.Level == "Junior");
        Assert.Equal(66, junior.DefaultPct);
        Assert.Equal(66, junior.EffectivePct);
        Assert.False(junior.IsOverridden);
    }

    // ── (6) LỘ TRÌNH ĐÃ COMPLETED ĐỌC SNAPSHOT — ngưỡng mới KHÔNG hồi tố ──────────────
    // Bất biến đắt nhất của tính năng này. Báo cáo đã phát cho người học không được âm thầm đổi
    // "Đạt" thành "Chưa đạt" vì admin vừa kéo ngưỡng lên.
    [Fact]
    public async Task RoadmapCompleted_DocSnapshot_KhongBiNguongMoiAnhHuong()
    {
        using var t = new TestDb();
        var user = Guid.NewGuid();

        // Snapshot chốt lúc đóng sổ: ngưỡng 60, tiêu chí 65% ⇒ ĐẠT.
        var snapshot = new RoadmapReportResponse(
            Radar: [],
            LevelEvaluation: [new RoadmapLevelEvaluationResponse("Clarity", 65m, 60, true)],
            Strengths: [],
            Weaknesses: [],
            Improvements: [],
            OverallComment: null,
            RoadmapStatus: RoadmapStatus.Active.ToString(),
            Progress: []);

        var roadmap = new Roadmap
        {
            Id = Guid.NewGuid(),
            CandidateId = user,
            JobCategory = JobCategory.BE,
            Level = RoadmapLevel.Junior,
            Status = RoadmapStatus.Completed,
            FinalReport = JsonSerializer.Serialize(snapshot, Json),
            CompletedAt = DateTime.UtcNow,
            CreatedAt = DateTime.UtcNow
        };
        t.Db.Roadmaps.Add(roadmap);
        await t.Db.SaveChangesAsync();

        // Admin kéo ngưỡng Junior lên 90 SAU khi lộ trình đã đóng sổ.
        await TestDb.Thresholds(t.Db).UpsertAsync(
            new Dictionary<string, int> { ["Junior"] = 90 }, Guid.NewGuid());

        var db = t.NewContext();
        var svc = new RoadmapReportService(
            db, new Mock<IAiServiceRoadmapGenerator>().Object,
            TestDb.Thresholds(db), NullLogger<RoadmapReportService>.Instance);

        var report = await svc.GetReportAsync(user, roadmap.Id);

        Assert.NotNull(report);
        var eval = Assert.Single(report!.LevelEvaluation);
        Assert.Equal(60, eval.LevelThreshold);   // ngưỡng ĐÃ CHỐT, không phải 90
        Assert.True(eval.Passed);                // và kết luận không bị lật
    }

    // ── (7) Lộ trình ĐANG ACTIVE thì dùng ngưỡng MỚI (đường tính lại on-read) ─────────
    // Vế đối chứng của (6): nếu ngưỡng mới không tới được đâu cả thì tính năng vô nghĩa — 65% với
    // mặc định 60 là ĐẠT; admin kéo lên 90 phải thành CHƯA ĐẠT.
    [Fact]
    public async Task RoadmapActive_DungNguongMoi()
    {
        using var t = new TestDb();
        var user = Guid.NewGuid();
        var sessionId = SeedScoredSession(t, user, "Clarity", 65m);

        var roadmap = new Roadmap
        {
            Id = Guid.NewGuid(),
            CandidateId = user,
            JobCategory = JobCategory.BE,
            Level = RoadmapLevel.Junior,   // mặc định 60
            Status = RoadmapStatus.Active,
            CreatedAt = DateTime.UtcNow
        };
        var milestone = new RoadmapMilestone
        {
            Id = Guid.NewGuid(),
            OrderNo = 1,
            Title = "M1",
            FocusCriteria = ["Clarity"],
            Status = MilestoneStatus.InProgress
        };
        milestone.Lessons.Add(new RoadmapLesson
        {
            Id = Guid.NewGuid(),
            OrderNo = 1,
            Title = "L1",
            Status = LessonStatus.Done,
            SessionId = sessionId
        });
        roadmap.Milestones.Add(milestone);
        t.Db.Roadmaps.Add(roadmap);
        await t.Db.SaveChangesAsync();

        // Đối chứng: chưa ai chỉnh ⇒ mặc định 60 ⇒ ĐẠT. Không có vế này thì test dưới có thể
        // "đúng" vì lý do khác (vd radar rỗng nên Single ném).
        var db0 = t.NewContext();
        var before = await new RoadmapReportService(
            db0, new Mock<IAiServiceRoadmapGenerator>().Object,
            TestDb.Thresholds(db0), NullLogger<RoadmapReportService>.Instance)
            .GetReportAsync(user, roadmap.Id);
        var evalBefore = Assert.Single(before!.LevelEvaluation);
        Assert.Equal(60, evalBefore.LevelThreshold);
        Assert.True(evalBefore.Passed);

        await TestDb.Thresholds(t.Db).UpsertAsync(
            new Dictionary<string, int> { ["Junior"] = 90 }, Guid.NewGuid());

        var db = t.NewContext();
        var report = await new RoadmapReportService(
            db, new Mock<IAiServiceRoadmapGenerator>().Object,
            TestDb.Thresholds(db), NullLogger<RoadmapReportService>.Instance)
            .GetReportAsync(user, roadmap.Id);

        var eval = Assert.Single(report!.LevelEvaluation);
        Assert.Equal(90, eval.LevelThreshold);
        Assert.False(eval.Passed);
    }

    // Buổi luyện ĐÃ CHẤM + breakdown BC9 (session_criterion_scores) — nguồn của radar/levelEvaluation.
    // criterion_id có FK Restrict về rubric_criteria nên phải seed tiêu chí trước.
    private static Guid SeedScoredSession(TestDb t, Guid cand, string criterionName, decimal pct)
    {
        var at = DateTime.UtcNow;
        var session = TestDb.Session(cand, SessionStatus.Scored, createdAt: at);
        var criterion = TestDb.Criterion(JobCategory.BE, name: criterionName);
        t.Db.PracticeSessions.Add(session);
        t.Db.RubricCriteria.Add(criterion);
        t.Db.SessionCriterionScores.Add(new SessionCriterionScore
        {
            Id = Guid.NewGuid(),
            SessionId = session.Id,
            CriterionId = criterion.Id,
            CriterionName = criterionName,
            AverageScore = Math.Round(pct / 20m, 2),   // MaxScore 5 ⇒ pct = avg/5*100
            MaxScore = 5,
            Percentage = pct,
            Weight = 1m,
            NeedsImprovement = pct < 50m,
            CreatedAt = at
        });
        t.Db.SaveChanges();
        return session.Id;
    }

    // ── (8) Cấp độ LẠ không làm đường đọc NÉM ────────────────────────────────────────
    // ThresholdForAsync nằm trên đường build report: ném ở đây là làm hỏng cả trang kết quả của
    // người học vì một dòng cấu hình thiếu.
    [Theory]
    [InlineData("Intern")]
    [InlineData("Lead")]
    [InlineData("")]
    [InlineData("khong-ton-tai")]
    public async Task CapDoLa_KhongNem_VaVeMacDinh(string level)
    {
        using var t = new TestDb();
        var pct = await TestDb.Thresholds(t.Db).ThresholdForAsync(level);
        Assert.InRange(pct, 0, 100);
    }

    // ── (9) PUT cấp độ LẠ → 400, KHÔNG ghi hàng chết ─────────────────────────────────
    // Mẫu F21 (PromptTemplateKeys): một hàng mang khoá không đường nào đọc sẽ khiến người sửa
    // tưởng mình vừa đổi được hành vi, trong khi report vẫn chạy ngưỡng cũ.
    [Fact]
    public async Task Put_CapDoLa_Tra400_KhongGhiHangChet()
    {
        using var t = new TestDb();
        var svc = TestDb.Thresholds(t.Db);

        var res = await Controller(svc, Guid.NewGuid()).UpdateAsync(
            new UpdateRoadmapLevelThresholdsRequest
            {
                Thresholds = new Dictionary<string, int> { ["Junor"] = 65 }
            }, default);

        var bad = Assert.IsType<BadRequestObjectResult>(res.Result);
        Assert.Contains("Junor", bad.Value!.ToString());
        Assert.Empty(await t.NewContext().RoadmapLevelThresholds.ToListAsync());
    }

    // ── (10) Chuỗi SỐ không được coi là cấp độ ───────────────────────────────────────
    // Enum.TryParse nhận "0" → Fresher; nếu dùng nó thì admin gõ nhầm vẫn "thành công" rồi sửa
    // trúng một cấp độ họ không định sửa.
    [Fact]
    public async Task Put_ChuoiSo_Tra400_KhongMapVeCapDauTien()
    {
        using var t = new TestDb();
        var svc = TestDb.Thresholds(t.Db);

        var res = await Controller(svc, Guid.NewGuid()).UpdateAsync(
            new UpdateRoadmapLevelThresholdsRequest
            {
                Thresholds = new Dictionary<string, int> { ["0"] = 99 }
            }, default);

        Assert.IsType<BadRequestObjectResult>(res.Result);
        Assert.Equal(50, await TestDb.Thresholds(t.NewContext()).ThresholdForAsync("Fresher"));
    }

    // ── (11) VALIDATE TOÀN BỘ TRƯỚC KHI GHI — sai một entry thì không entry nào được ghi ──
    // Admin nhận 400 mà nửa thay đổi đã nằm trong DB là trạng thái không ai kiểm tra lại,
    // vì "nó báo lỗi mà".
    [Fact]
    public async Task Put_MotEntrySai_ThiKhongEntryNaoDuocGhi()
    {
        using var t = new TestDb();
        var svc = TestDb.Thresholds(t.Db);

        var res = await Controller(svc, Guid.NewGuid()).UpdateAsync(
            new UpdateRoadmapLevelThresholdsRequest
            {
                Thresholds = new Dictionary<string, int> { ["Fresher"] = 45, ["Senior"] = 150 }
            }, default);

        Assert.IsType<BadRequestObjectResult>(res.Result);
        Assert.Empty(await t.NewContext().RoadmapLevelThresholds.ToListAsync());
        Assert.Equal(50, await TestDb.Thresholds(t.NewContext()).ThresholdForAsync("Fresher"));
    }

    // ── (12) Hoa/thường: nhận đầu vào, LƯU dạng chính tắc ────────────────────────────
    // Lưu "fresher" thường trong khi đường đọc hỏi "Fresher" là hỏng IM LẶNG: không lỗi, không
    // cảnh báo, admin tưởng đã chỉnh xong còn report vẫn dùng ngưỡng mặc định.
    [Fact]
    public async Task Put_KhongPhanBietHoaThuong_LuuDangChinhTac_DuongDocThayNgay()
    {
        using var t = new TestDb();
        await TestDb.Thresholds(t.Db).UpsertAsync(
            new Dictionary<string, int> { ["fReSheR"] = 42 }, Guid.NewGuid());

        var db = t.NewContext();
        Assert.Equal("Fresher", (await db.RoadmapLevelThresholds.SingleAsync()).Level);
        Assert.Equal(42, await TestDb.Thresholds(db).ThresholdForAsync("Fresher"));
    }

    // ── (13) Trùng cấp độ trong cùng body (khác hoa/thường) → 400 ───────────────────
    // Im lặng chọn một trong hai nghĩa là admin gửi hai con số và nhận về con số họ không chọn.
    [Fact]
    public async Task Put_TrungCapDoTrongCungBody_Tra400()
    {
        using var t = new TestDb();
        var svc = TestDb.Thresholds(t.Db);

        var res = await Controller(svc, Guid.NewGuid()).UpdateAsync(
            new UpdateRoadmapLevelThresholdsRequest
            {
                Thresholds = new Dictionary<string, int> { ["Fresher"] = 40, ["fresher"] = 55 }
            }, default);

        Assert.IsType<BadRequestObjectResult>(res.Result);
        Assert.Empty(await t.NewContext().RoadmapLevelThresholds.ToListAsync());
    }

    // ── (14) Body rỗng → 400 ────────────────────────────────────────────────────────
    [Fact]
    public async Task Put_BodyRong_Tra400()
    {
        using var t = new TestDb();
        var res = await Controller(TestDb.Thresholds(t.Db), Guid.NewGuid()).UpdateAsync(
            new UpdateRoadmapLevelThresholdsRequest { Thresholds = [] }, default);
        Assert.IsType<BadRequestObjectResult>(res.Result);
    }

    // ── (15) Biên 0 và 100 hợp lệ ───────────────────────────────────────────────────
    [Theory]
    [InlineData(0)]
    [InlineData(100)]
    public async Task Put_Bien0Va100_HopLe(int pct)
    {
        using var t = new TestDb();
        var svc = TestDb.Thresholds(t.Db);
        await svc.UpsertAsync(new Dictionary<string, int> { ["Middle"] = pct }, Guid.NewGuid());
        Assert.Equal(pct, await TestDb.Thresholds(t.NewContext()).ThresholdForAsync("Middle"));
    }

    // ── (16) Sửa lại cấp đã chỉnh = UPDATE tại chỗ, KHÔNG đẻ hàng thứ hai ───────────
    // Hai hàng cùng cấp thì đường đọc chọn hàng nào là không xác định.
    [Fact]
    public async Task Put_SuaLaiCapDaChinh_KhongDeHangThuHai()
    {
        using var t = new TestDb();
        var admin2 = Guid.NewGuid();
        var svc = TestDb.Thresholds(t.Db);

        await svc.UpsertAsync(new Dictionary<string, int> { ["Senior"] = 85 }, Guid.NewGuid());
        await svc.UpsertAsync(new Dictionary<string, int> { ["Senior"] = 88 }, admin2);

        var db = t.NewContext();
        var row = Assert.Single(await db.RoadmapLevelThresholds.ToListAsync());
        Assert.Equal(88, row.ThresholdPct);
        Assert.Equal(admin2, row.UpdatedBy);   // ghi vết = người sửa GẦN NHẤT
    }

    // ── (17) DELETE → quay về mặc định; cấp chưa ai chỉnh → 404; cấp lạ → 400 ───────
    [Fact]
    public async Task Delete_QuayVeMacDinh_ChuaChinhTra404_CapLaTra400()
    {
        using var t = new TestDb();
        var svc = TestDb.Thresholds(t.Db);
        var ctrl = Controller(svc, Guid.NewGuid());

        await svc.UpsertAsync(new Dictionary<string, int> { ["Middle"] = 95 }, Guid.NewGuid());
        Assert.Equal(95, await svc.ThresholdForAsync("Middle"));

        Assert.IsType<NoContentResult>(await ctrl.ResetAsync("Middle", default));

        var db = t.NewContext();
        Assert.Equal(70, await TestDb.Thresholds(db).ThresholdForAsync("Middle"));
        Assert.False((await TestDb.Thresholds(db).ListAsync()).Single(x => x.Level == "Middle").IsOverridden);

        Assert.IsType<NotFoundResult>(await ctrl.ResetAsync("Middle", default));
        Assert.IsType<BadRequestObjectResult>(await ctrl.ResetAsync("Junor", default));
    }

    // ── (18) Hàng MỒ CÔI (cấp bị gỡ khỏi enum) hiện ra để admin dọn ────────────────
    // Không đường nào đọc hàng đó nữa; giấu nó đi thì nó nằm vô hình trong bảng mãi mãi.
    [Fact]
    public async Task HangMoCoi_VanHienRa_KemCoIsKnownLevelFalse()
    {
        using var t = new TestDb();
        t.Db.RoadmapLevelThresholds.Add(new RoadmapLevelThreshold
        {
            Level = "CapDoDaBoDi",
            ThresholdPct = 77,
            UpdatedBy = Guid.NewGuid(),
            UpdatedAt = DateTime.UtcNow
        });
        await t.Db.SaveChangesAsync();

        var all = await TestDb.Thresholds(t.NewContext()).ListAsync();
        var orphan = all.Single(x => x.Level == "CapDoDaBoDi");
        Assert.False(orphan.IsKnownLevel);
        Assert.Equal(Enum.GetNames<RoadmapLevel>().Length + 1, all.Count);
    }
}
