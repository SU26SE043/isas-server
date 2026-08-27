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
/// REC1-B2 — bản gốc của file này (MIS1-era: "wizard cho candidate khai trình độ hiện tại ở bước
/// riêng, giá trị đó THẮNG giá trị suy từ CV") đã ĐỔI TIỀN ĐỀ một lần, rồi lại được thay bằng bản
/// "CV luôn thắng vô điều kiện qua CvAnalysisId".
///
/// 🔴 REC1-B7 — TIỀN ĐỀ ĐẢO LẦN THỨ HAI, đảo TOÀN BỘ 4 test cũ: khối `CvAnalysisId` (3 guard
/// 404/403/400 + tóm tắt/CurrentLevel làm ngữ cảnh) đã GỠ HẲN khỏi <see cref="RoadmapService.
/// CreateAsync"/>, cùng khối `PriorRoadmapId` và lời gọi kiểm quyền sở hữu `CvId`
/// (`ReadOwnedParsedTextAsync`). Bốn field <c>CvId</c>/<c>CvAnalysisId</c>/<c>PriorRoadmapId</c>/
/// <c>CurrentLevel</c> GIỮ NGUYÊN trên DTO (expand/contract — dọn ở đợt sau khi frontend ngừng
/// gửi) nhưng service BỎ QUA HOÀN TOÀN: gửi giá trị hợp lệ/lạ/không tồn tại/thuộc người khác đều
/// KHÔNG còn tạo ra 404/403/400 nào, và <c>currentLevel</c> không còn đường nào xuống được AI (đã
/// gỡ hẳn khỏi chữ ký <see cref="IAiServiceRoadmapGenerator.GenerateAsync"/>, không chỉ khỏi
/// payload). Lý do gỡ: prompt roadmap chỉ xuất ra CẤU TRÚC, mà cả CV lẫn lộ trình trước đều bị
/// chèn kèm câu "không đổi cấu trúc roadmap" — mệnh lệnh tự phủ định. Đo được: nhóm CÓ chọn CV nêu
/// công nghệ cụ thể ÍT hơn (8,6% vs 12,1%); lộ trình trước chỉ 4/37 đủ điều kiện trên dev, 0 trên
/// môi trường chính.
///
/// Toàn bộ 4 test cũ của file (đo "tự khai bị bỏ qua nhưng CV vẫn thắng có điều kiện") bị XOÁ —
/// tiền đề "CV có ảnh hưởng gì đó" không còn đúng nữa. Thay bằng các test đo ĐÚNG bất biến hiện
/// tại: XONG-KHI của REC1-B7 — "Gửi cvId/cvAnalysisId/priorRoadmapId/currentLevel ⇒ 201, bị bỏ qua."
/// </summary>
public class RoadmapCurrentLevelTests
{
    private static RoadmapGenAiResult SampleRoadmap()
        => new(new List<GeneratedMilestone>
        {
            new("M1", new List<string> { "Clarity" }, new List<GeneratedLesson> { new("L1") })
        });

    // REC1-B7 — chữ ký GenerateAsync gọn còn 9 tham số (jobCategory/level/weaknesses/focus/criteria/
    // scope/mode/ct/mistakes) — mất currentLevel nên không còn gì để capture qua tham số đó nữa;
    // mock chỉ cần trả về một roadmap hợp lệ để CreateAsync đi hết đường tới 201.
    private static Mock<IAiServiceRoadmapGenerator> GenMock()
    {
        var m = new Mock<IAiServiceRoadmapGenerator>();
        m.Setup(x => x.GenerateAsync(
                It.IsAny<string>(), It.IsAny<string>(),
                It.IsAny<IReadOnlyList<RoadmapWeakness>?>(),
                It.IsAny<string?>(),
                It.IsAny<IReadOnlyList<QuestionTargetCriterionDto>?>(), It.IsAny<string>(),
                It.IsAny<RoadmapMode>(), It.IsAny<CancellationToken>(),
                It.IsAny<IReadOnlyList<RoadmapMistake>?>()))
            .ReturnsAsync(SampleRoadmap());
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
    // không quan tâm NỘI DUNG tiêu chí (chỉ test 4 field bị bỏ qua) nên dùng 1 tiêu chí cố định.
    private static Guid SeedScoredSession(TestDb t, Guid candidateId)
        => TestSeed.ScoredSessionWithAnswers(t, candidateId, JobCategory.BE, seedContentMistakes: true, ("Clarity", 40m, true));

    private static Guid SeedCvAnalysis(TestDb t, Guid ownerId, string? currentLevel = "Middle")
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

    // ── XONG-KHI: gửi cvAnalysisId/priorRoadmapId/currentLevel ⇒ 201, bị bỏ qua ──────────────────

    [Fact]
    public async Task CvAnalysisId_KhongTonTai_VanTao201_KhongNem404()
    {
        using var t = new TestDb();
        var user = Guid.NewGuid();
        var sid = SeedScoredSession(t, user);
        var ctrl = Controller(t, GenMock().Object, user);

        var result = await ctrl.Create(
            new CreateRoadmapRequest(JobCategory.BE, RoadmapLevel.Junior, null,
                SessionIds: [sid], CvAnalysisId: Guid.NewGuid()),  // id KHÔNG tồn tại
            default);

        Assert.IsType<CreatedResult>(result);
    }

    [Fact]
    public async Task CvAnalysisId_CuaNguoiKhac_VanTao201_KhongNem403()
    {
        using var t = new TestDb();
        var user = Guid.NewGuid();
        var nguoiKhac = Guid.NewGuid();
        var caCuaNguoiKhac = SeedCvAnalysis(t, nguoiKhac);
        var sid = SeedScoredSession(t, user);
        var ctrl = Controller(t, GenMock().Object, user);

        var result = await ctrl.Create(
            new CreateRoadmapRequest(JobCategory.BE, RoadmapLevel.Junior, null,
                SessionIds: [sid], CvAnalysisId: caCuaNguoiKhac),  // id của NGƯỜI KHÁC
            default);

        Assert.IsType<CreatedResult>(result);
    }

    [Fact]
    public async Task PriorRoadmapId_KhongTonTai_VanTao201_KhongNem404()
    {
        using var t = new TestDb();
        var user = Guid.NewGuid();
        var sid = SeedScoredSession(t, user);
        var ctrl = Controller(t, GenMock().Object, user);

        var result = await ctrl.Create(
            new CreateRoadmapRequest(JobCategory.BE, RoadmapLevel.Junior, null,
                SessionIds: [sid], PriorRoadmapId: Guid.NewGuid()),  // id KHÔNG tồn tại
            default);

        Assert.IsType<CreatedResult>(result);
    }

    [Theory]
    [InlineData("Fresher")]
    [InlineData("Master")]      // không thuộc tập enum cũ
    [InlineData("")]            // chuỗi rỗng
    public async Task CurrentLevel_GuiGiTuyY_VanTao201_KhongNem400(string requested)
    {
        using var t = new TestDb();
        var user = Guid.NewGuid();
        var sid = SeedScoredSession(t, user);
        var ctrl = Controller(t, GenMock().Object, user);

        var result = await ctrl.Create(
            new CreateRoadmapRequest(JobCategory.BE, RoadmapLevel.Junior, null,
                SessionIds: [sid], CurrentLevel: requested),
            default);

        Assert.IsType<CreatedResult>(result);
    }

    [Fact]
    public async Task CvId_CuaNguoiKhac_VanTao201_KhongNem403()
    {
        // 🔴 REC1-B7 — ReadOwnedParsedTextAsync (kiểm quyền rồi vứt nội dung `_ =`) đã gỡ khỏi
        // CreateAsync: CvId nay CHỈ còn lưu xuống roadmaps.cv_id (FK Restrict → file_records) —
        // KHÔNG còn được xác thực CHỦ SỞ HỮU ở tầng service trước khi lưu. Dùng file THẬT SỰ TỒN
        // TẠI (thoả FK) nhưng thuộc NGƯỜI KHÁC — trước bản vá đây là ca 403, nay phải là 201.
        // (Không test CvId hoàn toàn không tồn tại: FK Restrict vẫn chặn ca đó, chỉ là chặn bằng
        // lỗi ràng buộc DB thay vì một câu 404 riêng — không thuộc phạm vi XONG-KHI của bước này.)
        using var t = new TestDb();
        var user = Guid.NewGuid();
        var nguoiKhac = Guid.NewGuid();
        var cvCuaNguoiKhac = Guid.NewGuid();
        t.Db.FileRecords.Add(new FileRecord
        {
            Id = cvCuaNguoiKhac,
            UserId = nguoiKhac,
            FileType = "CV",
            OriginalName = "cv.pdf",
            StoragePath = "cv/x.pdf",
            StorageBucket = "isas",
            MimeType = "application/pdf",
            FileSize = 100,
            ParsedText = "Nội dung CV của người khác",
            ParseStatus = "Done",
            CreatedAt = DateTime.UtcNow,
            UpdatedAt = DateTime.UtcNow,
        });
        var sid = SeedScoredSession(t, user);
        await t.Db.SaveChangesAsync();
        var ctrl = Controller(t, GenMock().Object, user);

        var result = await ctrl.Create(
            new CreateRoadmapRequest(JobCategory.BE, RoadmapLevel.Junior, CvId: cvCuaNguoiKhac,
                SessionIds: [sid]),
            default);

        Assert.IsType<CreatedResult>(result);
    }
}
