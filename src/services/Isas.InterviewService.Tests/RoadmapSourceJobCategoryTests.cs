using System.Security.Claims;
using System.Text.Json;
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
/// Nguồn dữ liệu của <c>POST /practice/roadmaps</c> phải CÙNG NGHỀ với lộ trình đang tạo.
///
/// <para>🔴 Trước bản vá, guard chọn buổi chỉ lọc theo id + chủ sở hữu + đủ điều kiện chấm
/// (<see cref="RoadmapSessionEligibility.Predicate"/>) — KHÔNG có một vế nào về nghề. Nên
/// <c>{jobCategory:"BE"}</c> kèm <c>sessionIds</c> của buổi BA trả 201: baseline/điểm yếu/bằng
/// chứng của nghề khác chảy vào prompt, trong khi danh sách tiêu chí gửi kèm lại là của BE. Cùng
/// lỗ đó có ở <c>cvAnalysisId</c> và <c>priorRoadmapId</c>. Frontend đã lọc phía client, nhưng
/// UI giấu đi ≠ hợp đồng từ chối.</para>
///
/// <para>Hai bất biến về THỨ TỰ, mỗi cái một test riêng vì chúng hỏng theo hai kiểu khác nhau:
/// <list type="bullet">
/// <item>guard lệch nghề đứng SAU cửa kiểm sở hữu — câu lỗi nêu đích danh id, nên id của người
/// khác phải tiếp tục rơi vào 404/403 câm, không được lộ ra qua câu 400.</item>
/// <item>guard lệch nghề đứng TRƯỚC các guard hạ nguồn (<c>Reinforce</c> thiếu điểm yếu, lộ trình
/// tham chiếu chưa có báo cáo) — nếu không, người dùng nhận một câu đúng-sự-thật nhưng sai
/// nguyên nhân rồi đi sửa nhầm chỗ.</item>
/// </list></para>
///
/// <para>Mọi ca từ chối còn khoá <c>Times.Never</c> trên generator (mẫu
/// <see cref="RoadmapReinforceModeTests"/>): guard phải chạy TRƯỚC lời gọi AI, nếu không người
/// dùng bị từ chối mà hệ thống vẫn đốt một lượt Gemini.</para>
/// </summary>
public class RoadmapSourceJobCategoryTests
{
    private const JobCategory Wanted = JobCategory.BE;
    private const JobCategory Other = JobCategory.BA;

    private static RoadmapGenAiResult Sample()
        => new(new List<GeneratedMilestone>
        {
            new("M1", new List<string> { "Tư duy giải quyết vấn đề" },
                new List<GeneratedLesson> { new("L1") })
        });

    // MIS1-B6 — thêm It.IsAny<IReadOnlyList<RoadmapMistake>?>() (13ᵗʰ tham số, MIS1-B5) khớp arity
    // interface hiện tại. Thiếu nó, Setup chỉ khớp lời gọi có `mistakes == null` theo NGHĨA ĐEN
    // (tham số optional thiếu trong expression tree biên dịch thành literal null) — mà Guard 3 nay
    // BẢO ĐẢM `mistakes` luôn khác null khi gọi thật, nên Setup không bao giờ khớp ⇒ Moq loose-mock
    // trả về Task<null> ⇒ NullReferenceException tại `ai.Milestones`.
    private static Mock<IAiServiceRoadmapGenerator> GenMock()
    {
        var m = new Mock<IAiServiceRoadmapGenerator>();
        m.Setup(x => x.GenerateAsync(
                It.IsAny<string>(), It.IsAny<string>(),
                It.IsAny<IReadOnlyList<RoadmapWeakness>?>(),
                It.IsAny<string?>(), It.IsAny<string?>(), It.IsAny<string?>(),
                It.IsAny<IReadOnlyList<QuestionTargetCriterionDto>?>(), It.IsAny<string>(),
                It.IsAny<IReadOnlyList<CriterionEvidence>?>(), It.IsAny<RoadmapMode>(),
                It.IsAny<string?>(), It.IsAny<CancellationToken>(),
                It.IsAny<IReadOnlyList<RoadmapMistake>?>()))
            .ReturnsAsync(Sample());
        return m;
    }

    private static void NeverCalledAi(Mock<IAiServiceRoadmapGenerator> gen)
        => gen.Verify(x => x.GenerateAsync(
            It.IsAny<string>(), It.IsAny<string>(),
            It.IsAny<IReadOnlyList<RoadmapWeakness>?>(),
            It.IsAny<string?>(), It.IsAny<string?>(), It.IsAny<string?>(),
            It.IsAny<IReadOnlyList<QuestionTargetCriterionDto>?>(), It.IsAny<string>(),
            It.IsAny<IReadOnlyList<CriterionEvidence>?>(), It.IsAny<RoadmapMode>(),
            It.IsAny<string?>(), It.IsAny<CancellationToken>()), Times.Never);

    private static RoadmapsController Controller(TestDb t, IAiServiceRoadmapGenerator gen, Guid userId)
    {
        var service = new RoadmapService(
            t.Db, new Mock<IStorageService>().Object, gen, NullLogger<RoadmapService>.Instance);
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

    /// <summary>Buổi B2C đã Scored + 1 tiêu chí có điểm (BC9). <paramref name="weak"/> quyết định
    /// buổi đó có điểm yếu hay không — dùng để tách bạch guard lệch nghề với guard điểm yếu.
    /// MIS1-B6 — thân hàm chuyển vào TestSeed.ScoredSessionWithAnswers (dùng chung 3 file); giữ
    /// NGUYÊN chữ ký để 12 call site trong file này không phải sửa. `seedContentMistakes: true` vì
    /// mọi test ở đây gọi CreateAsync (Guard 3 nay đòi ≥1 lỗi nội dung khi buổi có điểm yếu —
    /// xem TestSeed.cs).</summary>
    private static Guid SeedScoredSession(
        TestDb t, Guid owner, JobCategory cat, bool weak = true)
        => TestSeed.ScoredSessionWithAnswers(
            t, owner, cat, seedContentMistakes: true,
            ("Tư duy giải quyết vấn đề", weak ? 40m : 100m, weak));

    private static Guid SeedCvAnalysis(TestDb t, Guid owner, JobCategory cat)
    {
        var ca = new CvAnalysis
        {
            Id = Guid.NewGuid(),
            CandidateId = owner,
            CvId = Guid.NewGuid(),
            JobCategory = cat,
            Summary = "Tóm tắt CV.",
            CreatedAt = DateTime.UtcNow
        };
        t.Db.CvAnalyses.Add(ca);
        t.Db.SaveChanges();
        return ca.Id;
    }

    private static Guid SeedPriorRoadmap(
        TestDb t, Guid owner, JobCategory cat, bool withReport = true)
    {
        // final_report phải là RoadmapReportResponse thật (mẫu RoadmapTests.SeedPriorRoadmap) —
        // BuildPriorRoadmapSummary deserialize đúng kiểu đó, JSON tuỳ tiện sẽ ném NRE.
        var report = new RoadmapReportResponse(
            Radar: [], LevelEvaluation: [],
            Strengths: ["Giao tiếp tốt"], Weaknesses: ["Chưa sâu thuật toán"],
            Improvements: ["Luyện thêm quy hoạch động"],
            OverallComment: "Tiến bộ rõ rệt qua các buổi.",
            RoadmapStatus: nameof(Enums.RoadmapStatus.Completed),
            Progress: []);
        var r = new Roadmap
        {
            Id = Guid.NewGuid(),
            CandidateId = owner,
            JobCategory = cat,
            Level = RoadmapLevel.Junior,
            Status = withReport ? RoadmapStatus.Completed : RoadmapStatus.Active,
            FinalReport = withReport
                ? JsonSerializer.Serialize(report, new JsonSerializerOptions(JsonSerializerDefaults.Web))
                : null,
            CreatedAt = DateTime.UtcNow
        };
        t.Db.Roadmaps.Add(r);
        t.Db.SaveChanges();
        return r.Id;
    }

    private static CreateRoadmapRequest Req(
        IReadOnlyList<Guid>? sessionIds = null, Guid? cvAnalysisId = null,
        Guid? priorRoadmapId = null, string? mode = null)
        => new(Wanted, RoadmapLevel.Junior, null,
            SessionIds: sessionIds, CvAnalysisId: cvAnalysisId,
            PriorRoadmapId: priorRoadmapId, Mode: mode);

    private static string Message(IActionResult result)
        => Assert.IsType<BadRequestObjectResult>(result).Value!.ToString()!;

    // ── (1) Buổi luyện ──────────────────────────────────────────────────────────────────────

    [Fact]
    public async Task BuoiLuyenLechNghe_400_KhongGoiAI()
    {
        using var t = new TestDb();
        var user = Guid.NewGuid();
        var sid = SeedScoredSession(t, user, Other);
        var gen = GenMock();

        var result = await Controller(t, gen.Object, user).Create(Req(sessionIds: [sid]), default);

        var message = Message(result);
        // Câu lỗi phải nói CẢ nghề lệch LẪN id — người dùng cần biết bỏ chọn cái nào.
        Assert.Contains(Other.ToString(), message);
        Assert.Contains(Wanted.ToString(), message);
        Assert.Contains(sid.ToString(), message);
        NeverCalledAi(gen);
        Assert.Empty(await t.Db.Roadmaps.ToListAsync());
    }

    [Fact]
    public async Task BuoiLuyenLechNghe_MotTrongNhieu_VanChan_VaChiDichDanhCaiLech()
    {
        // Ca dễ lọt nhất nếu guard viết theo kiểu "tất cả đều lệch thì mới chặn": người dùng chọn
        // đa số buổi đúng nghề, lẫn MỘT buổi nghề khác.
        using var t = new TestDb();
        var user = Guid.NewGuid();
        var ok = SeedScoredSession(t, user, Wanted);
        var bad = SeedScoredSession(t, user, Other);
        var gen = GenMock();

        var result = await Controller(t, gen.Object, user).Create(Req(sessionIds: [ok, bad]), default);

        var message = Message(result);
        Assert.Contains(bad.ToString(), message);
        Assert.DoesNotContain(ok.ToString(), message);
        NeverCalledAi(gen);
    }

    [Fact]
    public async Task BuoiLuyenCungNghe_VanTaoDuoc()
    {
        // Đối chứng dương — thiếu nó thì một guard chặn TẤT CẢ cũng làm mọi test trên xanh.
        using var t = new TestDb();
        var user = Guid.NewGuid();
        var sid = SeedScoredSession(t, user, Wanted);

        var result = await Controller(t, GenMock().Object, user).Create(Req(sessionIds: [sid]), default);

        Assert.IsType<CreatedResult>(result);
        Assert.Single(await t.Db.Roadmaps.ToListAsync());
    }

    [Fact]
    public async Task BuoiLuyenLechNghe_CuaNguoiKhac_Van404Cam_KhongLoId()
    {
        // 🔴 Guard sở hữu PHẢI chạy trước: câu 400 nêu đích danh id, nên id của người khác lọt vào
        // đó là xác nhận "id này có thật" cho người không sở hữu nó. Guard 404 batch cố ý câm để
        // bịt đúng chỗ rò này — bản vá không được mở lại.
        using var t = new TestDb();
        var me = Guid.NewGuid();
        var someoneElse = Guid.NewGuid();
        var sid = SeedScoredSession(t, someoneElse, Other);
        var gen = GenMock();

        var result = await Controller(t, gen.Object, me).Create(Req(sessionIds: [sid]), default);

        var notFound = Assert.IsType<NotFoundObjectResult>(result);
        var message = notFound.Value!.ToString()!;
        Assert.DoesNotContain(sid.ToString(), message);
        Assert.DoesNotContain(Other.ToString(), message);
        NeverCalledAi(gen);
    }

    [Fact]
    public async Task BuoiLuyenLechNghe_ThangGuardReinforce_BaoDungNguyenNhan()
    {
        // 🔴 Buổi lệch nghề mà KHÔNG có tiêu chí nào cần cải thiện: nếu guard Reinforce chạy trước
        // thì người dùng nhận "hãy chọn buổi khác" — đúng lời khuyên, SAI nguyên nhân — rồi đi
        // chọn thêm buổi nghề khác nữa và vẫn hỏng.
        using var t = new TestDb();
        var user = Guid.NewGuid();
        var a = SeedScoredSession(t, user, Other, weak: false);
        var b = SeedScoredSession(t, user, Other, weak: false);
        var gen = GenMock();

        var result = await Controller(t, gen.Object, user)
            .Create(Req(sessionIds: [a, b], mode: nameof(RoadmapMode.Reinforce)), default);

        var message = Message(result);
        Assert.Contains(Other.ToString(), message);
        Assert.DoesNotContain("cần cải thiện", message);
        NeverCalledAi(gen);
    }

    // ── (2) Phân tích CV ────────────────────────────────────────────────────────────────────

    [Fact]
    public async Task PhanTichCvLechNghe_400_KhongGoiAI()
    {
        using var t = new TestDb();
        var user = Guid.NewGuid();
        var caId = SeedCvAnalysis(t, user, Other);
        var sid = SeedScoredSession(t, user, Wanted);   // MIS1-B6 — Guard 1/2/3
        var gen = GenMock();

        var result = await Controller(t, gen.Object, user).Create(Req([sid], cvAnalysisId: caId), default);

        var message = Message(result);
        Assert.Contains(Other.ToString(), message);
        Assert.Contains(caId.ToString(), message);
        NeverCalledAi(gen);
        Assert.Empty(await t.Db.Roadmaps.ToListAsync());
    }

    [Fact]
    public async Task PhanTichCvCungNghe_VanTaoDuoc()
    {
        using var t = new TestDb();
        var user = Guid.NewGuid();
        var caId = SeedCvAnalysis(t, user, Wanted);
        var sid = SeedScoredSession(t, user, Wanted);   // MIS1-B6 — Guard 1/2/3

        var result = await Controller(t, GenMock().Object, user).Create(Req([sid], cvAnalysisId: caId), default);

        Assert.IsType<CreatedResult>(result);
    }

    [Fact]
    public async Task PhanTichCvLechNghe_CuaNguoiKhac_Van403_KhongLoId()
    {
        using var t = new TestDb();
        var caller = Guid.NewGuid();
        var caId = SeedCvAnalysis(t, Guid.NewGuid(), Other);
        var sid = SeedScoredSession(t, caller, Wanted);   // MIS1-B6 — Guard 1/2/3
        var gen = GenMock();

        var result = await Controller(t, gen.Object, caller).Create(Req([sid], cvAnalysisId: caId), default);

        var forbidden = Assert.IsType<ObjectResult>(result);
        Assert.Equal(StatusCodes.Status403Forbidden, forbidden.StatusCode);
        Assert.DoesNotContain(caId.ToString(), forbidden.Value!.ToString()!);
        NeverCalledAi(gen);
    }

    // ── (3) Lộ trình tham chiếu ─────────────────────────────────────────────────────────────

    [Fact]
    public async Task LoTrinhThamChieuLechNghe_400_KhongGoiAI()
    {
        using var t = new TestDb();
        var user = Guid.NewGuid();
        var priorId = SeedPriorRoadmap(t, user, Other);
        var sid = SeedScoredSession(t, user, Wanted);   // MIS1-B6 — Guard 1/2/3
        var gen = GenMock();

        var result = await Controller(t, gen.Object, user).Create(Req([sid], priorRoadmapId: priorId), default);

        var message = Message(result);
        Assert.Contains(Other.ToString(), message);
        Assert.Contains(priorId.ToString(), message);
        NeverCalledAi(gen);
        // Chỉ còn ĐÚNG lộ trình seed — không có lộ trình mới nào được tạo.
        Assert.Single(await t.Db.Roadmaps.ToListAsync());
    }

    [Fact]
    public async Task LoTrinhThamChieuCungNghe_VanTaoDuoc()
    {
        using var t = new TestDb();
        var user = Guid.NewGuid();
        var priorId = SeedPriorRoadmap(t, user, Wanted);
        var sid = SeedScoredSession(t, user, Wanted);   // MIS1-B6 — Guard 1/2/3

        var result = await Controller(t, GenMock().Object, user).Create(Req([sid], priorRoadmapId: priorId), default);

        Assert.IsType<CreatedResult>(result);
        Assert.Equal(2, (await t.Db.Roadmaps.ToListAsync()).Count);
    }

    [Fact]
    public async Task LoTrinhThamChieuLechNghe_ThangGuardChuaCoBaoCao()
    {
        // 🔴 Cùng lý do với ca Reinforce ở trên: lộ trình nghề khác CHƯA hoàn thành mà báo "chưa
        // có báo cáo" sẽ khiến người dùng đi hoàn thành nó rồi quay lại — và vẫn hỏng, vì nguyên
        // nhân thật là lệch nghề.
        using var t = new TestDb();
        var user = Guid.NewGuid();
        var priorId = SeedPriorRoadmap(t, user, Other, withReport: false);
        var sid = SeedScoredSession(t, user, Wanted);   // MIS1-B6 — Guard 1/2/3
        var gen = GenMock();

        var result = await Controller(t, gen.Object, user).Create(Req([sid], priorRoadmapId: priorId), default);

        var message = Message(result);
        Assert.Contains(Other.ToString(), message);
        Assert.DoesNotContain("chưa có báo cáo", message);
        NeverCalledAi(gen);
    }

    [Fact]
    public async Task LoTrinhThamChieuLechNghe_CuaNguoiKhac_Van403_KhongLoId()
    {
        using var t = new TestDb();
        var caller = Guid.NewGuid();
        var priorId = SeedPriorRoadmap(t, Guid.NewGuid(), Other);
        var sid = SeedScoredSession(t, caller, Wanted);   // MIS1-B6 — Guard 1/2/3
        var gen = GenMock();

        var result = await Controller(t, gen.Object, caller)
            .Create(Req([sid], priorRoadmapId: priorId), default);

        var forbidden = Assert.IsType<ObjectResult>(result);
        Assert.Equal(StatusCodes.Status403Forbidden, forbidden.StatusCode);
        Assert.DoesNotContain(priorId.ToString(), forbidden.Value!.ToString()!);
        NeverCalledAi(gen);
    }
}
