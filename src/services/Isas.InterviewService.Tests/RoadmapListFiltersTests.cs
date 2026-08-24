using System.Security.Claims;
using Isas.InterviewService.Controllers;
using Isas.InterviewService.Entities;
using Isas.InterviewService.Enums;
using Isas.InterviewService.Services;
using Isas.InterviewService.Services.Interfaces;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc;
using Microsoft.Extensions.Logging.Abstractions;
using Moq;

namespace Isas.InterviewService.Tests;

/// <summary>
/// Wizard tạo lộ trình có bước "chọn lộ trình đã hoàn tất" (gửi <c>priorRoadmapId</c>). Trước bản
/// này <c>GET /roadmaps</c> trả MỌI lộ trình và client tự lọc trên TRANG ĐẦU của keyset paging ⇒
/// người có nhiều lộ trình thì cái hợp lệ nằm ngoài trang đầu biến mất khỏi dropdown mà không ai
/// biết: không lỗi, không dòng trống, chỉ là một lựa chọn đáng lẽ có mà không thấy.
///
/// <para>Hai filter <c>?status=</c> + <c>?hasFinalReport=</c> là OPT-IN, chạy TRONG SQL trước khi
/// cắt trang. <c>hasFinalReport</c> mới là vị ngữ đúng nghiệp vụ — xem
/// <see cref="RoadmapHasFinalReportTests"/> về lý do <c>status == Completed</c> lọc sai.</para>
/// </summary>
public class RoadmapListFiltersTests
{
    private static RoadmapService Service(TestDb t)
        => new(t.Db, new Mock<IStorageService>().Object,
               new Mock<IAiServiceRoadmapGenerator>().Object, NullLogger<RoadmapService>.Instance);

    private static Roadmap Row(Guid owner, RoadmapStatus status, string? finalReport, DateTime createdAt)
        => new()
        {
            Id = Guid.NewGuid(),
            CandidateId = owner,
            JobCategory = JobCategory.BE,
            Level = RoadmapLevel.Middle,
            Status = status,
            FinalReport = finalReport,
            CreatedAt = createdAt
        };

    private const string Report = "{\"overallComment\":\"xong\"}";

    /// <summary>
    /// 3 lộ trình phủ đúng 3 ca picker phải phân biệt: đang chạy · hoàn tất CÓ báo cáo · từng hoàn
    /// tất nhưng bị <c>RetryLessonAsync</c> mở lại và XOÁ báo cáo (status vẫn Completed).
    /// </summary>
    private static async Task<(Guid dangChay, Guid coBaoCao, Guid completedMatBaoCao)> SeedMixed(TestDb t, Guid user)
    {
        var now = DateTime.UtcNow;
        var dangChay = Row(user, RoadmapStatus.Active, null, now);
        var coBaoCao = Row(user, RoadmapStatus.Completed, Report, now.AddMinutes(-1));
        var completedMatBaoCao = Row(user, RoadmapStatus.Completed, null, now.AddMinutes(-2));
        t.Db.AddRange(dangChay, coBaoCao, completedMatBaoCao);
        await t.Db.SaveChangesAsync();
        return (dangChay.Id, coBaoCao.Id, completedMatBaoCao.Id);
    }

    // ── (1) Không tham số → hành vi cũ, y hệt hôm nay ─────────────────────────────────────
    [Fact]
    public async Task KhongThamSo_TraTatCa_GiongHanhViCu()
    {
        using var t = new TestDb();
        var user = Guid.NewGuid();
        await SeedMixed(t, user);

        var page = await Service(t).ListAsync(user);

        Assert.Equal(3, page.Items.Count);
    }

    // ── (2) ?status=Completed → lọc đúng trạng thái ───────────────────────────────────────
    [Fact]
    public async Task Status_Completed_LocDungTrangThai()
    {
        using var t = new TestDb();
        var user = Guid.NewGuid();
        var (dangChay, coBaoCao, completedMatBaoCao) = await SeedMixed(t, user);

        var page = await Service(t).ListAsync(user, status: "Completed");

        Assert.Equal(2, page.Items.Count);
        Assert.Contains(page.Items, x => x.Id == coBaoCao);
        Assert.Contains(page.Items, x => x.Id == completedMatBaoCao);
        Assert.DoesNotContain(page.Items, x => x.Id == dangChay);
    }

    // ── (3) CA QUYẾT ĐỊNH: hasFinalReport KHÁC status==Completed ──────────────────────────
    //
    // Lộ trình `completedMatBaoCao` lọt qua bộ lọc status nhưng `CreateAsync` sẽ trả 400 — tức picker
    // lọc theo status mời người dùng chọn một thứ rồi bắt họ ăn lỗi SAU KHI đã chờ 13–54s tạo roadmap.
    [Fact]
    public async Task HasFinalReport_True_LoaiCaLoTrinhCompletedMaMatBaoCao()
    {
        using var t = new TestDb();
        var user = Guid.NewGuid();
        var (dangChay, coBaoCao, completedMatBaoCao) = await SeedMixed(t, user);

        var page = await Service(t).ListAsync(user, hasFinalReport: true);

        var only = Assert.Single(page.Items);
        Assert.Equal(coBaoCao, only.Id);
        Assert.True(only.HasFinalReport);
        Assert.DoesNotContain(page.Items, x => x.Id == completedMatBaoCao);
        Assert.DoesNotContain(page.Items, x => x.Id == dangChay);
    }

    // hasFinalReport=false (tường minh) = mặt bù, KHÁC vắng mặt.
    [Fact]
    public async Task HasFinalReport_False_ChiTraLoTrinhChuaCoBaoCao()
    {
        using var t = new TestDb();
        var user = Guid.NewGuid();
        var (dangChay, coBaoCao, completedMatBaoCao) = await SeedMixed(t, user);

        var page = await Service(t).ListAsync(user, hasFinalReport: false);

        Assert.Equal(2, page.Items.Count);
        Assert.Contains(page.Items, x => x.Id == dangChay);
        Assert.Contains(page.Items, x => x.Id == completedMatBaoCao);
        Assert.DoesNotContain(page.Items, x => x.Id == coBaoCao);
        Assert.All(page.Items, x => Assert.False(x.HasFinalReport));
    }

    // Vắng ⇒ KHÔNG lọc (trang "Lộ trình của tôi" dùng chính endpoint này).
    [Fact]
    public async Task HasFinalReport_Vang_KhongLocGi()
    {
        using var t = new TestDb();
        var user = Guid.NewGuid();
        await SeedMixed(t, user);

        var page = await Service(t).ListAsync(user, hasFinalReport: null);

        Assert.Equal(3, page.Items.Count);
    }

    // ── (4) CA QUYẾT ĐỊNH: lọc phải chạy TRƯỚC khi cắt trang ──────────────────────────────
    //
    // Đây chính là con bug đang có ở client: lọc SAU khi lấy trang đầu. Lộ trình duy nhất có báo cáo
    // được seed CŨ NHẤT nên nó nằm ngoài trang đầu (thứ tự CreatedAt DESC) — lọc sau phân trang sẽ
    // trả về RỖNG, lọc trong SQL thì trả về đúng nó.
    [Fact]
    public async Task HasFinalReport_LocTruocPhanTrang_KhongMatLoTrinhNgoaiTrangDau()
    {
        using var t = new TestDb();
        var user = Guid.NewGuid();
        var now = DateTime.UtcNow;
        var moiNhat = Row(user, RoadmapStatus.Active, null, now);
        var moiNhi = Row(user, RoadmapStatus.Active, null, now.AddMinutes(-1));
        var cuNhatCoBaoCao = Row(user, RoadmapStatus.Completed, Report, now.AddMinutes(-2));
        t.Db.AddRange(moiNhat, moiNhi, cuNhatCoBaoCao);
        await t.Db.SaveChangesAsync();

        var page = await Service(t).ListAsync(user, limit: 2, hasFinalReport: true);

        Assert.Equal(cuNhatCoBaoCao.Id, Assert.Single(page.Items).Id);
    }

    // Kết hợp cả hai — đúng ca picker của wizard cần.
    [Fact]
    public async Task Status_VaHasFinalReport_KetHop_DungCaWizardCan()
    {
        using var t = new TestDb();
        var user = Guid.NewGuid();
        var (_, coBaoCao, _) = await SeedMixed(t, user);

        var page = await Service(t).ListAsync(user, status: "Completed", hasFinalReport: true);

        Assert.Equal(coBaoCao, Assert.Single(page.Items).Id);
    }

    // ── (5) Giá trị lạ → từ chối TƯỜNG MINH, không nuốt im lặng ──────────────────────────
    [Theory]
    [InlineData("KhongTonTai")]
    [InlineData("completed")]     // sai hoa/thường — KHÔNG nhận (mẫu ValidateMode/ValidateHistoryStatus)
    [InlineData("1")]             // chuỗi số — `Enum.TryParse` vốn nhận, ta KHÔNG hứa hỗ trợ
    public async Task Status_GiaTriLa_TuChoiTuongMinh(string status)
    {
        using var t = new TestDb();
        var user = Guid.NewGuid();
        await SeedMixed(t, user);

        var ex = await Assert.ThrowsAsync<InvalidOperationException>(
            () => Service(t).ListAsync(user, status: status));
        Assert.Contains(status, ex.Message);   // câu lỗi phải nêu giá trị đang gửi
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("   ")]
    public async Task Status_VangHoacRong_KhongLocGi(string? status)
    {
        using var t = new TestDb();
        var user = Guid.NewGuid();
        await SeedMixed(t, user);

        var page = await Service(t).ListAsync(user, status: status);

        Assert.Equal(3, page.Items.Count);
    }

    // 🔑 Ném `InvalidOperationException` chỉ có nghĩa nếu controller map nó thành 400. Action `List`
    // trước đó KHÔNG bắt gì cả ⇒ thiếu nhánh bắt thì siết validate lại biến `?status=xyz` thành 500
    // — tệ hơn hẳn fail-open. Đúng lớp lỗi F2b, và là bẫy đã phải vá một lần ở `GetHistory`.
    [Fact]
    public async Task Controller_StatusGiaTriLa_Tra400_KhongPhai500()
    {
        using var t = new TestDb();
        var user = Guid.NewGuid();
        await SeedMixed(t, user);

        var ctrl = new RoadmapsController(
            Service(t), new Mock<IRoadmapLessonService>().Object,
            new Mock<IRoadmapReportService>().Object, NullLogger<RoadmapsController>.Instance)
        {
            ControllerContext = new ControllerContext
            {
                HttpContext = new DefaultHttpContext
                {
                    User = new ClaimsPrincipal(new ClaimsIdentity(
                        [new Claim(ClaimTypes.NameIdentifier, user.ToString())], "test"))
                }
            }
        };

        var result = await ctrl.List(default, status: "KhongTonTai");

        Assert.IsType<BadRequestObjectResult>(result);
    }

    // Đường HỢP LỆ qua controller vẫn 200 — để nhánh catch ở trên không âm thầm nuốt ca đúng.
    [Fact]
    public async Task Controller_LocHopLe_Tra200()
    {
        using var t = new TestDb();
        var user = Guid.NewGuid();
        var (_, coBaoCao, _) = await SeedMixed(t, user);

        var ctrl = new RoadmapsController(
            Service(t), new Mock<IRoadmapLessonService>().Object,
            new Mock<IRoadmapReportService>().Object, NullLogger<RoadmapsController>.Instance)
        {
            ControllerContext = new ControllerContext
            {
                HttpContext = new DefaultHttpContext
                {
                    User = new ClaimsPrincipal(new ClaimsIdentity(
                        [new Claim(ClaimTypes.NameIdentifier, user.ToString())], "test"))
                }
            }
        };

        var result = await ctrl.List(default, status: "Completed", hasFinalReport: true);

        var ok = Assert.IsType<OkObjectResult>(result);
        var items = Assert.IsAssignableFrom<IReadOnlyList<Isas.InterviewService.DTOs.RoadmapSummaryResponse>>(ok.Value);
        Assert.Equal(coBaoCao, Assert.Single(items).Id);
    }
}
