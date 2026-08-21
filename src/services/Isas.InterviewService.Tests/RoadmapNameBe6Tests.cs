using System.Globalization;
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

// BE-6 — tên lộ trình. Trước bản này backend không có tên nào, nên frontend rơi vào nhánh dự phòng
// `|| 'Roadmap'` (roadmapMapper.ts:204) 100% số lần: ba lộ trình hiện ba thẻ giống hệt nhau.
//
// Bốn tính chất được khoá ở đây:
//   (1) không tự đặt → server sinh tên; tự đặt → dùng ĐÚNG tên đó, không bị ghi đè
//   (2) chuỗi rỗng/toàn khoảng trắng/quá dài → 400, KHÔNG âm thầm rơi về tên máy sinh (lớp lỗi BK36)
//   (3) `name` có ở CẢ chi tiết LẪN danh sách — thiếu list thì đúng chỗ vấn đề lộ ra vẫn hỏng
//   (4) hàng cũ (`name` null) vẫn trả ra tên dùng được, null không bao giờ chảy ra API
public class RoadmapNameBe6Tests
{
    private static RoadmapGenAiResult Sample()
        => new([new GeneratedMilestone("Chặng 1", ["Phân tích yêu cầu"], [new GeneratedLesson("Bài 1")])]);

    private static Mock<IAiServiceRoadmapGenerator> Gen()
    {
        var m = new Mock<IAiServiceRoadmapGenerator>();
        m.Setup(x => x.GenerateAsync(
                It.IsAny<string>(), It.IsAny<string>(),
                It.IsAny<IReadOnlyList<RoadmapWeakness>?>(), It.IsAny<string?>(),
                It.IsAny<string?>(), It.IsAny<string?>(), It.IsAny<string?>(),
                It.IsAny<IReadOnlyList<QuestionTargetCriterionDto>?>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(Sample());
        return m;
    }

    private static RoadmapService Service(TestDb t)
        => new(t.Db, new Mock<IStorageService>().Object, Gen().Object, NullLogger<RoadmapService>.Instance);

    private static RoadmapsController Controller(TestDb t, Guid userId)
    {
        var c = new RoadmapsController(
            Service(t), new Mock<IRoadmapLessonService>().Object,
            new Mock<IRoadmapReportService>().Object, NullLogger<RoadmapsController>.Instance);
        c.ControllerContext = new ControllerContext
        {
            HttpContext = new DefaultHttpContext
            {
                User = new ClaimsPrincipal(new ClaimsIdentity(
                    [new Claim(ClaimTypes.NameIdentifier, userId.ToString())], "test"))
            }
        };
        return c;
    }

    // ══════════════════════════════════════════════════════════════════════════
    // (1) Tên mặc định do server sinh · tên người dùng không bị ghi đè
    // ══════════════════════════════════════════════════════════════════════════

    [Fact]
    public async Task KhongTuDatTen_ServerSinhTenCoNgheVaCapDo()
    {
        using var t = new TestDb();
        var user = Guid.NewGuid();

        var res = await Service(t).CreateAsync(user,
            new CreateRoadmapRequest(JobCategory.BA, RoadmapLevel.Senior, null));

        Assert.Contains("Business Analyst", res.Name);
        Assert.Contains("Senior", res.Name);
        Assert.NotEqual("Roadmap", res.Name);   // đúng chuỗi dự phòng cũ của frontend
    }

    [Fact]
    public async Task TuDatTen_ServerKhongGhiDe()
    {
        using var t = new TestDb();
        var user = Guid.NewGuid();

        var res = await Service(t).CreateAsync(user,
            new CreateRoadmapRequest(JobCategory.BE, RoadmapLevel.Junior, null, Name: "Ôn phỏng vấn FPT"));

        Assert.Equal("Ôn phỏng vấn FPT", res.Name);
        Assert.Equal("Ôn phỏng vấn FPT", (await t.Db.Set<Roadmap>().SingleAsync()).Name);
    }

    [Fact]
    public async Task TenCoKhoangTrangThua_DuocCatTruocKhiLuu()
    {
        using var t = new TestDb();
        var res = await Service(t).CreateAsync(Guid.NewGuid(),
            new CreateRoadmapRequest(JobCategory.FE, RoadmapLevel.Middle, null, Name: "   Lộ trình của tôi   "));

        Assert.Equal("Lộ trình của tôi", res.Name);
    }

    // ══════════════════════════════════════════════════════════════════════════
    // (2) Đầu vào sai → 400, không âm thầm rơi về tên máy sinh
    // ══════════════════════════════════════════════════════════════════════════

    [Theory]
    [InlineData("")]
    [InlineData("   ")]
    [InlineData("\t\n")]
    public async Task TenRong_BiTuChoi_KhongAmThamRoiVeTenMaySinh(string name)
    {
        using var t = new TestDb();
        var svc = Service(t);

        await Assert.ThrowsAsync<InvalidOperationException>(() => svc.CreateAsync(Guid.NewGuid(),
            new CreateRoadmapRequest(JobCategory.BA, RoadmapLevel.Fresher, null, Name: name)));

        // Không được tạo row nào — đầu vào sai thì dừng TRƯỚC khi ghi.
        Assert.Empty(await t.Db.Set<Roadmap>().ToListAsync());
    }

    [Fact]
    public async Task TenQuaDai_BiTuChoi()
    {
        using var t = new TestDb();
        var qua = new string('x', RoadmapNaming.MaxLength + 1);

        await Assert.ThrowsAsync<InvalidOperationException>(() => Service(t).CreateAsync(Guid.NewGuid(),
            new CreateRoadmapRequest(JobCategory.BA, RoadmapLevel.Fresher, null, Name: qua)));
    }

    [Fact]
    public async Task TenDaiVuaTran_VanDuocChapNhan()
    {
        using var t = new TestDb();
        var vua = new string('x', RoadmapNaming.MaxLength);

        var res = await Service(t).CreateAsync(Guid.NewGuid(),
            new CreateRoadmapRequest(JobCategory.BA, RoadmapLevel.Fresher, null, Name: vua));

        Assert.Equal(vua, res.Name);
    }

    // ══════════════════════════════════════════════════════════════════════════
    // (3) `name` phải có ở CẢ hai đường đọc
    // ══════════════════════════════════════════════════════════════════════════

    [Fact]
    public async Task DanhSach_CungTraTen_KhongChiRiengChiTiet()
    {
        using var t = new TestDb();
        var user = Guid.NewGuid();
        var svc = Service(t);
        await svc.CreateAsync(user, new CreateRoadmapRequest(JobCategory.BA, RoadmapLevel.Senior, null, Name: "Lộ trình A"));

        var page = await svc.ListAsync(user, null, null);

        Assert.Equal("Lộ trình A", Assert.Single(page.Items).Name);
    }

    // ══════════════════════════════════════════════════════════════════════════
    // (4) Hàng cũ `name` null → suy tên lúc đọc, null không chảy ra API
    // ══════════════════════════════════════════════════════════════════════════

    [Fact]
    public async Task HangCuKhongCoTen_VanTraRaTenDungDuoc_CaChiTietLanDanhSach()
    {
        using var t = new TestDb();
        var user = Guid.NewGuid();
        var id = Guid.NewGuid();
        t.Db.Set<Roadmap>().Add(new Roadmap
        {
            Id = id,
            CandidateId = user,
            Name = null,                       // hàng tạo TRƯỚC BE-6
            JobCategory = JobCategory.FE,
            Level = RoadmapLevel.Middle,
            Language = "vi",
            Status = RoadmapStatus.Active,
            CreatedAt = new DateTime(2026, 8, 21, 0, 0, 0, DateTimeKind.Utc)
        });
        await t.Db.SaveChangesAsync();

        var svc = Service(t);
        var chiTiet = await svc.GetAsync(user, id);
        var danhSach = await svc.ListAsync(user, null, null);

        Assert.False(string.IsNullOrWhiteSpace(chiTiet!.Name));
        Assert.Contains("Frontend Developer", chiTiet.Name);
        Assert.Equal(chiTiet.Name, Assert.Single(danhSach.Items).Name);   // hai đường đọc phải khớp
    }

    // ══════════════════════════════════════════════════════════════════════════
    // Ngày trong tên theo NGÔN NGỮ LỘ TRÌNH, không theo culture của tiến trình
    // ══════════════════════════════════════════════════════════════════════════

    [Fact]
    public void NgayTheoNgonNguLoTrinh_KhongTheoCultureCuaMayChu()
    {
        var ngay = new DateTime(2026, 8, 21, 0, 0, 0, DateTimeKind.Utc);
        var truoc = CultureInfo.CurrentCulture;
        try
        {
            // Giả lập máy chủ chạy culture khác hẳn. Repo đã dính đúng lỗi này (F16): PDF in `91,5`
            // còn CSV in `91.5` cho cùng một campaign vì cả hai đọc culture của tiến trình.
            CultureInfo.CurrentCulture = CultureInfo.GetCultureInfo("de-DE");

            Assert.Equal("Lộ trình Business Analyst · Senior · 21/08/2026",
                RoadmapNaming.BuildDefault(JobCategory.BA, RoadmapLevel.Senior, "vi", ngay));
            Assert.Equal("Business Analyst roadmap · Senior · 21 Aug 2026",
                RoadmapNaming.BuildDefault(JobCategory.BA, RoadmapLevel.Senior, "en", ngay));
        }
        finally
        {
            CultureInfo.CurrentCulture = truoc;
        }
    }

    // ══════════════════════════════════════════════════════════════════════════
    // PATCH — đổi tên
    // ══════════════════════════════════════════════════════════════════════════

    [Fact]
    public async Task DoiTen_ChuSoHuu_LuuDuocVaTraVeTenMoi()
    {
        using var t = new TestDb();
        var user = Guid.NewGuid();
        var tao = await Service(t).CreateAsync(user, new CreateRoadmapRequest(JobCategory.BA, RoadmapLevel.Senior, null));

        var res = await Controller(t, user).Rename(tao.Id, new RenameRoadmapRequest("Tên mới"), default);

        var ok = Assert.IsType<OkObjectResult>(res);
        Assert.Equal("Tên mới", Assert.IsType<RoadmapResponse>(ok.Value).Name);
        Assert.Equal("Tên mới", (await t.Db.Set<Roadmap>().SingleAsync()).Name);
    }

    [Fact]
    public async Task DoiTen_KhongPhaiChuSoHuu_403_VaKhongDoiGiTrongDb()
    {
        using var t = new TestDb();
        var chu = Guid.NewGuid();
        var nguoiLa = Guid.NewGuid();
        var tao = await Service(t).CreateAsync(chu, new CreateRoadmapRequest(JobCategory.BA, RoadmapLevel.Senior, null, Name: "Của tôi"));

        var res = await Controller(t, nguoiLa).Rename(tao.Id, new RenameRoadmapRequest("Cướp"), default);

        Assert.Equal(StatusCodes.Status403Forbidden, Assert.IsType<ObjectResult>(res).StatusCode);
        Assert.Equal("Của tôi", (await t.Db.Set<Roadmap>().SingleAsync()).Name);
    }

    [Fact]
    public async Task DoiTen_RoadmapKhongTonTai_404()
    {
        using var t = new TestDb();
        var res = await Controller(t, Guid.NewGuid()).Rename(Guid.NewGuid(), new RenameRoadmapRequest("X"), default);

        Assert.IsType<NotFoundObjectResult>(res);
    }

    [Theory]
    [InlineData("")]
    [InlineData("   ")]
    public async Task DoiTen_TenRong_400_VaGiuNguyenTenCu(string name)
    {
        using var t = new TestDb();
        var user = Guid.NewGuid();
        var tao = await Service(t).CreateAsync(user, new CreateRoadmapRequest(JobCategory.BA, RoadmapLevel.Senior, null, Name: "Tên cũ"));

        var res = await Controller(t, user).Rename(tao.Id, new RenameRoadmapRequest(name), default);

        Assert.IsType<BadRequestObjectResult>(res);
        Assert.Equal("Tên cũ", (await t.Db.Set<Roadmap>().SingleAsync()).Name);
    }

    [Fact]
    public async Task DoiTen_LoTrinhDaHoanThanh_VanDoiDuoc()
    {
        using var t = new TestDb();
        var user = Guid.NewGuid();
        var id = Guid.NewGuid();
        t.Db.Set<Roadmap>().Add(new Roadmap
        {
            Id = id,
            CandidateId = user,
            Name = "Xong rồi",
            JobCategory = JobCategory.BA,
            Level = RoadmapLevel.Senior,
            Language = "vi",
            Status = RoadmapStatus.Completed,      // tên là nhãn người dùng, không bị đóng băng theo kết quả
            CreatedAt = DateTime.UtcNow,
            CompletedAt = DateTime.UtcNow
        });
        await t.Db.SaveChangesAsync();

        var res = await Controller(t, user).Rename(id, new RenameRoadmapRequest("Đổi sau khi xong"), default);

        Assert.IsType<OkObjectResult>(res);
        Assert.Equal("Đổi sau khi xong", (await t.Db.Set<Roadmap>().SingleAsync()).Name);
    }
}
