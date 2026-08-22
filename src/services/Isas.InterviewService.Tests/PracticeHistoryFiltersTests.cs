using Isas.InterviewService.ApplicationDbContext;
using Isas.InterviewService.Entities;
using Isas.InterviewService.Enums;
using Isas.InterviewService.Services;
using Isas.InterviewService.Services.Interfaces;
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

    // Giá trị lạ — fail-open ĐÚNG MẪU ListAllCampaignsAsync (CampaignService): `Enum.TryParse`
    // thất bại ⇒ filter KHÔNG được áp (bỏ qua, không lọc gì), KHÔNG 400 (đây là filter
    // duyệt-danh-sách, khác RoadmapService.ValidateCurrentLevel — input đó dẫn nghiệp vụ/tốn
    // một lượt Gemini nên phải từ chối tường minh; ở đây gõ sai chỉ mất tác dụng lọc, không
    // gây hậu quả gì đáng phải chặn cứng).
    [Fact]
    public async Task Status_GiaTriLa_KhongLocGi_TraTatCa()
    {
        using var t = new TestDb();
        var candidate = Guid.NewGuid();
        await SeedMixed(t, candidate);

        var page = await BuildPractice(t.Db).GetHistoryAsync(candidate, status: "KhongTonTai");

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
