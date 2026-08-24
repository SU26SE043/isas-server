using System.Security.Claims;
using Isas.InterviewService.ApplicationDbContext;
using Isas.InterviewService.Controllers;
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
/// Wizard tạo roadmap dựng picker buổi luyện từ <c>GET /practice/history</c>, nhưng
/// <c>RoadmapService.CreateAsync</c> chỉ nhận buổi B2C (<c>campaign_id == null</c>) ĐÃ Scored — và
/// thiếu bất kỳ id nào thì trả 404 batch không nói id nào sai. Hai filter <c>?status=</c> +
/// <c>?excludeCampaign=</c> là OPT-IN để picker chỉ đưa buổi hợp lệ ra cho người dùng chọn, KHÔNG
/// đổi hành vi mặc định (trang "Lịch sử phỏng vấn" đang dùng chính endpoint này).
/// </summary>
public class PracticeHistoryFiltersTests
{
    private static PracticeService BuildPractice(InterviewDbContext db)
        => new(db,
            new Mock<IStorageService>().Object,
            new Mock<IAiServiceQuestionGenerator>().Object,
            new Mock<ISessionScoringNotifier>().Object,
            new Mock<ICreditReservationClient>().Object,
            NullLogger<PracticeService>.Instance);

    private static async Task<(Guid scoredB2C, Guid inProgressB2C, Guid scoredB2B)> SeedMixed(TestDb t, Guid candidate)
    {
        var now = DateTime.UtcNow;
        var scoredB2C = TestDb.Session(candidate, SessionStatus.Scored, createdAt: now);
        var inProgressB2C = TestDb.Session(candidate, SessionStatus.InProgress, createdAt: now.AddMinutes(-1));
        var scoredB2B = TestDb.Session(
            candidate, SessionStatus.Scored, campaignId: Guid.NewGuid(), createdAt: now.AddMinutes(-2));
        t.Db.AddRange(scoredB2C, inProgressB2C, scoredB2B);
        await t.Db.SaveChangesAsync();
        return (scoredB2C.Id, inProgressB2C.Id, scoredB2B.Id);
    }

    // ── (1) Không tham số → hành vi cũ, y hệt hôm nay (shape + tập kết quả không đổi) ───
    [Fact]
    public async Task KhongThamSo_TraTatCa_GiongHanhViCu()
    {
        using var t = new TestDb();
        var candidate = Guid.NewGuid();
        var (scoredB2C, inProgressB2C, scoredB2B) = await SeedMixed(t, candidate);

        var page = await BuildPractice(t.Db).GetHistoryAsync(candidate);

        Assert.Equal(3, page.Items.Count);
        Assert.Contains(page.Items, x => x.Id == scoredB2C);
        Assert.Contains(page.Items, x => x.Id == inProgressB2C);
        Assert.Contains(page.Items, x => x.Id == scoredB2B);
    }

    // ── (2) ?status=Scored → lọc đúng trạng thái ─────────────────────────────────────────
    [Fact]
    public async Task Status_Scored_ChiTraBuoiDaCham()
    {
        using var t = new TestDb();
        var candidate = Guid.NewGuid();
        var (scoredB2C, inProgressB2C, scoredB2B) = await SeedMixed(t, candidate);

        var page = await BuildPractice(t.Db).GetHistoryAsync(candidate, status: "Scored");

        Assert.Equal(2, page.Items.Count);
        Assert.Contains(page.Items, x => x.Id == scoredB2C);
        Assert.Contains(page.Items, x => x.Id == scoredB2B);
        Assert.DoesNotContain(page.Items, x => x.Id == inProgressB2C);
    }

    // ⚠ TIỀN ĐỀ ĐÃ ĐẢO (22/08). Bản đầu khoá hành vi FAIL-OPEN theo mẫu `ListAllCampaignsAsync`
    // (CampaignService): `Enum.TryParse` thất bại ⇒ bỏ qua filter, không 400 — lập luận khi đó là
    // "filter duyệt-danh-sách, gõ sai chỉ mất tác dụng lọc, không hậu quả".
    //
    // Lập luận đó KHÔNG đứng vững cho ĐÚNG endpoint này: nó còn nuôi PICKER của wizard roadmap.
    // "Không lọc được" ở đây không vô hại — nó trả lại buổi B2B/chưa chấm cho người dùng CHỌN, rồi
    // `RoadmapService.CreateAsync` từ chối bằng 404 batch KHÔNG nói id nào sai. Tức nuốt im lặng
    // tái sinh đúng con bug mà hai filter này sinh ra để diệt. Mẫu CampaignService vẫn đúng cho
    // CHÍNH NÓ (không có guard downstream nào ăn theo) — khác bối cảnh, không phải khác chuẩn.
    [Theory]
    [InlineData("KhongTonTai")]
    [InlineData("scored")]        // sai hoa/thường — KHÔNG nhận (mẫu ValidateMode/ValidateCurrentLevel)
    [InlineData("3")]             // chuỗi số — `Enum.TryParse` vốn nhận, ta KHÔNG hứa hỗ trợ
    public async Task Status_GiaTriLa_TuChoiTuongMinh(string status)
    {
        using var t = new TestDb();
        var candidate = Guid.NewGuid();
        await SeedMixed(t, candidate);

        var ex = await Assert.ThrowsAsync<InvalidOperationException>(
            () => BuildPractice(t.Db).GetHistoryAsync(candidate, status: status));
        Assert.Contains(status, ex.Message);   // câu lỗi phải nêu giá trị đang gửi
    }

    // 🔑 Ném `InvalidOperationException` chỉ có nghĩa nếu controller map nó thành 400. Action
    // `GetHistory` trước đó CHỈ bắt `UnauthorizedAccessException` ⇒ thiếu nhánh bắt thì siết
    // validate lại biến `?status=xyz` thành 500 — tệ hơn hẳn fail-open. Đúng lớp lỗi F2b.
    [Fact]
    public async Task Controller_StatusGiaTriLa_Tra400_KhongPhai500()
    {
        using var t = new TestDb();
        var candidate = Guid.NewGuid();
        await SeedMixed(t, candidate);

        var ctrl = new PracticeController(BuildPractice(t.Db), Mock.Of<IQuestionSpeechService>(),
            NullLogger<PracticeController>.Instance)
        {
            ControllerContext = new ControllerContext
            {
                HttpContext = new DefaultHttpContext
                {
                    User = new ClaimsPrincipal(new ClaimsIdentity(
                        [new Claim(ClaimTypes.NameIdentifier, candidate.ToString())], "test"))
                }
            }
        };

        var result = await ctrl.GetHistory(default, status: "KhongTonTai");

        Assert.IsType<BadRequestObjectResult>(result);
    }

    // Vắng/rỗng ⇒ KHÔNG lọc — hành vi mặc định của trang Lịch sử phỏng vấn giữ nguyên.
    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("   ")]
    public async Task Status_VangHoacRong_KhongLocGi(string? status)
    {
        using var t = new TestDb();
        var candidate = Guid.NewGuid();
        await SeedMixed(t, candidate);

        var page = await BuildPractice(t.Db).GetHistoryAsync(candidate, status: status);

        Assert.Equal(3, page.Items.Count);
    }

    // ── (3) ?excludeCampaign=true → loại buổi B2B ────────────────────────────────────────
    [Fact]
    public async Task ExcludeCampaign_True_LoaiBuoiB2B()
    {
        using var t = new TestDb();
        var candidate = Guid.NewGuid();
        var (scoredB2C, inProgressB2C, scoredB2B) = await SeedMixed(t, candidate);

        var page = await BuildPractice(t.Db).GetHistoryAsync(candidate, excludeCampaign: true);

        Assert.Equal(2, page.Items.Count);
        Assert.Contains(page.Items, x => x.Id == scoredB2C);
        Assert.Contains(page.Items, x => x.Id == inProgressB2C);
        Assert.DoesNotContain(page.Items, x => x.Id == scoredB2B);
    }

    // excludeCampaign=false (tường minh) khác vắng mặt — vẫn phải giữ buổi B2B, không lọc.
    [Fact]
    public async Task ExcludeCampaign_False_KhongLoaiGi()
    {
        using var t = new TestDb();
        var candidate = Guid.NewGuid();
        var (scoredB2C, inProgressB2C, scoredB2B) = await SeedMixed(t, candidate);

        var page = await BuildPractice(t.Db).GetHistoryAsync(candidate, excludeCampaign: false);

        Assert.Equal(3, page.Items.Count);
    }

    // ── (4) Kết hợp cả hai — đúng ca wizard roadmap cần: buổi B2C đã Scored ─────────────
    [Fact]
    public async Task Status_Scored_VaExcludeCampaign_KetHop_DungCaWizardCan()
    {
        using var t = new TestDb();
        var candidate = Guid.NewGuid();
        var (scoredB2C, inProgressB2C, scoredB2B) = await SeedMixed(t, candidate);

        var page = await BuildPractice(t.Db)
            .GetHistoryAsync(candidate, status: "Scored", excludeCampaign: true);

        var only = Assert.Single(page.Items);
        Assert.Equal(scoredB2C, only.Id);
    }
}
