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
/// REC1-B2 — ĐỔI TIỀN ĐỀ hoàn toàn so với bản gốc của file này (MIS1-era: "wizard cho candidate
/// khai trình độ hiện tại ở bước riêng, giá trị đó THẮNG giá trị suy từ CV"). `ValidateCurrentLevel`
/// + khối `currentLevelOverride` đã bị GỠ khỏi <see cref="RoadmapService.CreateAsync"/> — "trình độ
/// hiện tại" đi xuống AI nay CHỈ còn suy từ <c>cv_analyses.CurrentLevel</c> (qua <c>CvAnalysisId</c>).
///
/// <para><c>req.CurrentLevel</c> (client tự khai) KHÔNG CÒN ĐƯỢC ĐỌC Ở BẤT KỲ ĐÂU — không xác thực
/// (không còn 400 dù gửi giá trị lạ), không override CV, không tác dụng. Field vẫn tồn tại trên DTO
/// (backward-compat cho FE cũ), nhưng gửi gì vào đó cũng như không gửi.</para>
///
/// <para>Bốn test dưới đây (1)(2)(4)(5) của bản gốc bị XOÁ vì đo đúng cơ chế đã gỡ (tự khai thắng
/// CV / validate 400 giá trị lạ / validate chạy trước I/O) — không còn gì để đo. Thay bằng 3 test
/// mới đo ĐÚNG bất biến hiện tại: tự khai bị bỏ qua HOÀN TOÀN (dù giá trị "hợp lệ" theo enum cũ hay
/// "lạ" tuỳ tiện), và CV luôn thắng vô điều kiện. Test (3) <c>CurrentLevel_Null_GiuHanhViCu_SuyTuCvAnalysis</c>
/// GIỮ NGUYÊN — hành vi "suy từ CV khi có" chưa từng đổi, chỉ là nó không còn cạnh tranh với ai.</para>
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
            It.IsAny<string?>(), It.IsAny<CancellationToken>(),
            It.IsAny<IReadOnlyList<RoadmapMistake>?>()));
        if (captureCurrentLevel is not null)
            setup.Callback<string, string, IReadOnlyList<RoadmapWeakness>?, string?, string?, string?,
                    IReadOnlyList<QuestionTargetCriterionDto>?, string, IReadOnlyList<CriterionEvidence>?,
                    RoadmapMode, string?, CancellationToken, IReadOnlyList<RoadmapMistake>?>(
                    (_, _, _, _, _, _, _, _, _, _, cur, _, _) => captureCurrentLevel(cur))
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

    // MIS1-B6 — Guard 1/2/3 đòi ≥1 buổi đã chấm, có điểm yếu, có lỗi nội dung trích được. File này
    // không quan tâm NỘI DUNG tiêu chí (chỉ test currentLevel) nên dùng 1 tiêu chí cố định.
    private static Guid SeedScoredSession(TestDb t, Guid candidateId)
        => TestSeed.ScoredSessionWithAnswers(t, candidateId, JobCategory.BE, seedContentMistakes: true, ("Clarity", 40m, true));

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

    // ── (1) Tự khai — dù "hợp lệ" theo enum cũ hay lạ tuỳ tiện — KHÔNG còn đi xuống AI ───────────
    //
    // Gộp hai bài test cũ (CurrentLevel_HopLe_DiXuongAi + CurrentLevel_GiaTriLa_Nem400_...) thành
    // MỘT bất biến: không còn khác biệt nào giữa "giá trị hợp lệ" và "giá trị lạ" nữa, vì cả hai
    // đều không được đọc. Không có CvAnalysisId ⇒ captured PHẢI là null bất kể client gửi gì.
    [Theory]
    [InlineData("Fresher")]
    [InlineData("Senior")]
    [InlineData("fresher")]     // sai hoa/thường — trước đây 400, nay chỉ đơn giản bị lờ đi
    [InlineData("Master")]      // không thuộc tập cũ
    [InlineData("")]            // chuỗi rỗng
    [InlineData("1")]
    public async Task TuKhai_KhongConDuocDoc_CapturedNullKhiKhongCoCv(string requested)
    {
        using var t = new TestDb();
        var user = Guid.NewGuid();
        var sid = SeedScoredSession(t, user);   // MIS1-B6 — Guard 1/2/3
        string? captured = "chưa gán";
        var ctrl = Controller(t, GenMock(v => captured = v).Object, user);

        var result = await ctrl.Create(
            new CreateRoadmapRequest(JobCategory.BE, RoadmapLevel.Junior, null,
                SessionIds: [sid], CurrentLevel: requested),
            default);

        // Roadmap VẪN tạo được (201) — garbage currentLevel không còn là lỗi đầu vào.
        Assert.IsType<CreatedResult>(result);
        Assert.Null(captured);
    }

    // ── (2) null → giữ hành vi cũ: suy từ cv_analyses khi có CvAnalysisId ────────────────
    //
    // GIỮ NGUYÊN từ bản gốc — hành vi "suy từ CV khi có, null khi không" KHÔNG đổi; chỉ là nó
    // không còn phải cạnh tranh ưu tiên với giá trị tự khai của người dùng nữa.
    [Fact]
    public async Task CurrentLevel_Null_GiuHanhViCu_SuyTuCvAnalysis()
    {
        using var t = new TestDb();
        var user = Guid.NewGuid();
        var caId = SeedCvAnalysis(t, user, currentLevel: "Middle");
        var sid = SeedScoredSession(t, user);   // MIS1-B6 — Guard 1/2/3
        string? captured = null;
        var ctrl = Controller(t, GenMock(v => captured = v).Object, user);

        var result = await ctrl.Create(
            new CreateRoadmapRequest(JobCategory.BE, RoadmapLevel.Junior, null, SessionIds: [sid], CvAnalysisId: caId), default);

        Assert.IsType<CreatedResult>(result);
        Assert.Equal("Middle", captured);
    }

    // ── (3) CV LUÔN THẮNG — vô điều kiện, kể cả khi client gửi kèm currentLevel khác ─────────────
    //
    // Đảo NGƯỢC bài test cũ "người dùng gửi thắng CV": trước đây "Fresher" (client) phải thắng
    // "Senior" (CV); nay CV luôn thắng vì client không còn đường nào can thiệp. Hai giá trị KHÁC
    // NHAU có chủ đích — nếu cơ chế cũ hồi sinh (bug regression), test này bắt được ngay.
    [Fact]
    public async Task CvAnalysis_LuonThang_KeCaKhiClientGuiKemCurrentLevelKhac()
    {
        using var t = new TestDb();
        var user = Guid.NewGuid();
        var caId = SeedCvAnalysis(t, user, currentLevel: "Senior");
        var sid = SeedScoredSession(t, user);   // MIS1-B6 — Guard 1/2/3
        string? captured = null;
        var ctrl = Controller(t, GenMock(v => captured = v).Object, user);

        var result = await ctrl.Create(
            new CreateRoadmapRequest(
                JobCategory.BE, RoadmapLevel.Junior, null,
                SessionIds: [sid], CvAnalysisId: caId, CurrentLevel: "Fresher"),
            default);

        Assert.IsType<CreatedResult>(result);
        Assert.Equal("Senior", captured);   // của CV — KHÔNG PHẢI "Fresher" client gửi
    }

    // ── (4) Không chọn CV nào — tự khai vẫn KHÔNG đi xuống AI (không còn lối vòng nào) ───────────
    [Fact]
    public async Task KhongChonCv_TuKhaiVanKhongDiXuongAi_CapturedNull()
    {
        using var t = new TestDb();
        var user = Guid.NewGuid();
        var sid = SeedScoredSession(t, user);   // MIS1-B6 — Guard 1/2/3
        string? captured = "chưa gán";
        var ctrl = Controller(t, GenMock(v => captured = v).Object, user);

        var result = await ctrl.Create(
            new CreateRoadmapRequest(
                JobCategory.BE, RoadmapLevel.Junior, null,
                SessionIds: [sid], CvAnalysisId: null, CurrentLevel: "Middle"),
            default);

        Assert.IsType<CreatedResult>(result);
        Assert.Null(captured);
    }
}
