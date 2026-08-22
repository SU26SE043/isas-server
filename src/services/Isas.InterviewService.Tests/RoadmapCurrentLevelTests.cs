using System.Security.Claims;
using Isas.InterviewService.Controllers;
using Isas.InterviewService.DTOs;
using Isas.InterviewService.Entities;
using Isas.InterviewService.Enums;
using Isas.InterviewService.Services;
using Isas.InterviewService.Services.Interfaces;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging.Abstractions;
using Moq;

namespace Isas.InterviewService.Tests;

/// <summary>
/// Wizard tạo roadmap cho candidate KHAI trình độ hiện tại ở bước riêng, thay vì chỉ suy ngầm từ
/// <c>cvAnalysisId</c>. Mẫu <see cref="RoadmapReinforceModeTests"/>/<see cref="RoadmapModeWireTests"/>:
/// tập đóng, case-sensitive, chỉ <c>null</c> mới rơi về hành vi cũ (BK36 — không âm thầm nuốt giá
/// trị lạ). Giá trị người dùng khai PHẢI THẮNG giá trị suy từ <c>cv_analyses</c>.
/// </summary>
public class RoadmapCurrentLevelTests
{
    private static RoadmapGenAiResult SampleRoadmap()
        => new(new List<GeneratedMilestone>
        {
            new("M1", new List<string> { "Clarity" }, new List<GeneratedLesson> { new("L1") })
        });

    private static Mock<IAiServiceRoadmapGenerator> GenMock(Action<string?>? captureCurrentLevel = null)
    {
        var m = new Mock<IAiServiceRoadmapGenerator>();
        var setup = m.Setup(x => x.GenerateAsync(
            It.IsAny<string>(), It.IsAny<string>(),
            It.IsAny<IReadOnlyList<RoadmapWeakness>?>(),
            It.IsAny<string?>(), It.IsAny<string?>(), It.IsAny<string?>(),
            It.IsAny<IReadOnlyList<QuestionTargetCriterionDto>?>(), It.IsAny<string>(),
            It.IsAny<IReadOnlyList<CriterionEvidence>?>(), It.IsAny<RoadmapMode>(),
            It.IsAny<string?>(), It.IsAny<CancellationToken>()));
        if (captureCurrentLevel is not null)
            setup.Callback<string, string, IReadOnlyList<RoadmapWeakness>?, string?, string?, string?,
                    IReadOnlyList<QuestionTargetCriterionDto>?, string, IReadOnlyList<CriterionEvidence>?,
                    RoadmapMode, string?, CancellationToken>(
                    (_, _, _, _, _, _, _, _, _, _, cur, _) => captureCurrentLevel(cur))
                .ReturnsAsync(SampleRoadmap());
        else
            setup.ReturnsAsync(SampleRoadmap());
        return m;
    }

    private static RoadmapsController Controller(TestDb t, IAiServiceRoadmapGenerator gen, Guid userId)
    {
        var service = new RoadmapService(t.Db, new Mock<IStorageService>().Object, gen, NullLogger<RoadmapService>.Instance);
        var controller = new RoadmapsController(
            service, new Mock<IRoadmapLessonService>().Object,
            new Mock<IRoadmapReportService>().Object, NullLogger<RoadmapsController>.Instance);
        controller.ControllerContext = new ControllerContext
        {
            HttpContext = new DefaultHttpContext
            {
                User = new ClaimsPrincipal(new ClaimsIdentity(
                    [new Claim(ClaimTypes.NameIdentifier, userId.ToString())], "test"))
            }
        };
        return controller;
    }

    private static Guid SeedCvAnalysis(TestDb t, Guid ownerId, string? currentLevel)
    {
        var ca = new CvAnalysis
        {
            Id = Guid.NewGuid(),
            CandidateId = ownerId,
            CvId = Guid.NewGuid(),
            JobCategory = JobCategory.BE,
            Summary = "Ứng viên backend.",
            CurrentLevel = currentLevel,
            CreatedAt = DateTime.UtcNow
        };
        t.Db.CvAnalyses.Add(ca);
        t.Db.SaveChanges();
        return ca.Id;
    }

    // ── (1) Giá trị hợp lệ được nhận và chuyển xuống AI ─────────────────────────────────
    [Theory]
    [InlineData("Fresher")]
    [InlineData("Junior")]
    [InlineData("Middle")]
    [InlineData("Senior")]
    public async Task CurrentLevel_HopLe_DiXuongAi(string level)
    {
        using var t = new TestDb();
        var user = Guid.NewGuid();
        string? captured = null;
        var ctrl = Controller(t, GenMock(v => captured = v).Object, user);

        var result = await ctrl.Create(
            new CreateRoadmapRequest(JobCategory.BE, RoadmapLevel.Junior, null, CurrentLevel: level), default);

        Assert.IsType<CreatedResult>(result);
        Assert.Equal(level, captured);
    }

    // ── (2) Giá trị lạ → 400, kèm giá trị đang gửi ───────────────────────────────────────
    [Theory]
    [InlineData("fresher")]      // sai hoa/thường — mẫu ValidateMode: KHÔNG chấp nhận
    [InlineData("Master")]       // không thuộc tập
    [InlineData("")]             // chuỗi rỗng — GIÁ TRỊ SAI, khác null
    [InlineData("1")]            // Enum.TryParse sẽ chấp nhận — so khớp tường minh thì không
    public async Task CurrentLevel_GiaTriLa_Nem400_KemGiaTriDangGui(string invalid)
    {
        using var t = new TestDb();
        var user = Guid.NewGuid();
        var gen = GenMock();
        var ctrl = Controller(t, gen.Object, user);

        var result = await ctrl.Create(
            new CreateRoadmapRequest(JobCategory.BE, RoadmapLevel.Junior, null, CurrentLevel: invalid), default);

        var bad = Assert.IsType<BadRequestObjectResult>(result);
        var message = bad.Value!.ToString()!;
        Assert.Contains(invalid, message);
        Assert.Contains("Fresher", message);
        Assert.Contains("Senior", message);
    }

    // ── (3) null → giữ hành vi cũ: suy từ cv_analyses khi có CvAnalysisId ────────────────
    [Fact]
    public async Task CurrentLevel_Null_GiuHanhViCu_SuyTuCvAnalysis()
    {
        using var t = new TestDb();
        var user = Guid.NewGuid();
        var caId = SeedCvAnalysis(t, user, currentLevel: "Middle");
        string? captured = null;
        var ctrl = Controller(t, GenMock(v => captured = v).Object, user);

        var result = await ctrl.Create(
            new CreateRoadmapRequest(JobCategory.BE, RoadmapLevel.Junior, null, CvAnalysisId: caId), default);

        Assert.IsType<CreatedResult>(result);
        Assert.Equal("Middle", captured);
    }

    // ── (4) Người dùng gửi THẮNG giá trị suy từ cv_analyses ──────────────────────────────
    [Fact]
    public async Task CurrentLevel_NguoiDungGui_ThangGiaTriTuCvAnalysis()
    {
        using var t = new TestDb();
        var user = Guid.NewGuid();
        // Hai giá trị KHÁC NHAU có chủ đích — nếu ưu tiên bị đảo, test này bắt được ngay.
        var caId = SeedCvAnalysis(t, user, currentLevel: "Senior");
        string? captured = null;
        var ctrl = Controller(t, GenMock(v => captured = v).Object, user);

        var result = await ctrl.Create(
            new CreateRoadmapRequest(
                JobCategory.BE, RoadmapLevel.Junior, null,
                CvAnalysisId: caId, CurrentLevel: "Fresher"),
            default);

        Assert.IsType<CreatedResult>(result);
        Assert.Equal("Fresher", captured);   // của NGƯỜI DÙNG, không phải "Senior" từ CV
    }

    // ── (4b) 🔴 KHÔNG chọn CV nào — currentLevel vẫn phải chảy tới AI ────────────────────
    //
    // Bổ sung sau khi giao brief: nếu merge "người dùng thắng CV" bị đặt NGƯỢC — vào TRONG khối
    // `if (req.CvAnalysisId is not null)` thay vì chạy vô điều kiện — thì candidate KHÔNG chọn
    // bản phân tích CV nào (bỏ qua bước CV, nhánh hợp lệ đã chốt trong wizard) sẽ có lựa chọn ở
    // bước "Trình độ hiện tại" bị RƠI IM LẶNG: không lỗi gì, chỉ là giá trị không bao giờ tới AI.
    [Fact]
    public async Task CurrentLevel_KhongChonCvAnalysis_VanDiXuongAi()
    {
        using var t = new TestDb();
        var user = Guid.NewGuid();
        string? captured = null;
        var ctrl = Controller(t, GenMock(v => captured = v).Object, user);

        var result = await ctrl.Create(
            new CreateRoadmapRequest(
                JobCategory.BE, RoadmapLevel.Junior, null,
                CvAnalysisId: null, CurrentLevel: "Middle"),
            default);

        Assert.IsType<CreatedResult>(result);
        Assert.Equal("Middle", captured);
    }

    // ── (5) Validate chạy TRƯỚC mọi I/O — không đốt lượt AI khi giá trị lạ ───────────────
    [Fact]
    public async Task CurrentLevel_GiaTriLa_KhongGoiAi_KhongLuuRoadmap()
    {
        using var t = new TestDb();
        var user = Guid.NewGuid();
        var gen = GenMock();
        var ctrl = Controller(t, gen.Object, user);

        var result = await ctrl.Create(
            new CreateRoadmapRequest(JobCategory.BE, RoadmapLevel.Junior, null, CurrentLevel: "Master"),
            default);

        Assert.IsType<BadRequestObjectResult>(result);
        gen.Verify(x => x.GenerateAsync(
            It.IsAny<string>(), It.IsAny<string>(), It.IsAny<IReadOnlyList<RoadmapWeakness>?>(),
            It.IsAny<string?>(), It.IsAny<string?>(), It.IsAny<string?>(),
            It.IsAny<IReadOnlyList<QuestionTargetCriterionDto>?>(), It.IsAny<string>(),
            It.IsAny<IReadOnlyList<CriterionEvidence>?>(), It.IsAny<RoadmapMode>(),
            It.IsAny<string?>(), It.IsAny<CancellationToken>()), Times.Never);
        Assert.False(await t.Db.Roadmaps.AsNoTracking().AnyAsync());
    }
}
