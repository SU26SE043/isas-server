using Isas.CampaignService.DTOs;
using Isas.CampaignService.Models;
using Isas.CampaignService.Services;
using Microsoft.Extensions.Logging;
using Moq;
using CampaignSvc = Isas.CampaignService.Services.CampaignService;

namespace Isas.CampaignService.Tests;

/// <summary>
/// Ca thi phải nằm TRONG cửa sổ mở của chiến dịch.
///
/// <para>Trước bản này <c>ValidateSlot</c> chỉ kiểm <c>ends &gt; starts</c> và <c>capacity &gt; 0</c>.
/// HR tạo được ca ngoài cửa sổ, và không thấy gì bất thường cho tới khi ứng viên đã được gán vào
/// đó rồi không bao giờ thi được — lúc ấy <c>ParticipationService</c> mới chặn, bằng một câu nói
/// về CA chứ không chỉ ra nguyên nhân là cửa sổ CHIẾN DỊCH.</para>
/// </summary>
public class CampaignSlotWindowGuardTests
{
    private static CampaignSvc Service(CampaignDbContext db) => new(db, Mock.Of<IFileService>(),
        Mock.Of<ILogger<CampaignSvc>>(), Mock.Of<IParserService>(), Mock.Of<ICriteriaSuggester>(), Mock.Of<IInvitationEmailPublisher>());

    private static readonly DateTime Open = new(2099, 1, 10, 0, 0, 0, DateTimeKind.Utc);
    private static readonly DateTime Close = new(2099, 1, 20, 0, 0, 0, DateTimeKind.Utc);

    private static Campaign Windowed(Guid orgId, DateTime? open, DateTime? close)
    {
        var campaign = CampaignTestDb.NewCampaign(orgId);
        campaign.StartsAt = open;
        campaign.ExpiresAt = close;
        return campaign;
    }

    private static CreateCampaignSlotRequest Create(DateTime start, DateTime end) =>
        new() { StartsAt = start, EndsAt = end, Capacity = 2 };

    private static UpdateCampaignSlotRequest Update(DateTime start, DateTime end) =>
        new() { StartsAt = start, EndsAt = end, Capacity = 2 };

    [Fact]
    public async Task Create_CaNamTronTrongCuaSo_ThiTaoDuoc()
    {
        using var t = new CampaignTestDb();
        var owner = Guid.NewGuid();
        var campaign = Windowed(owner, Open, Close);
        t.Db.Campaigns.Add(campaign);
        await t.Db.SaveChangesAsync();

        var created = await Service(t.NewContext())
            .CreateSlotAsync(owner, campaign.Id, Create(Open.AddDays(2), Open.AddDays(2).AddHours(1)), default);

        Assert.Equal(2, created.Capacity);
    }

    [Fact]
    public async Task Create_CaTrungDungMocMoVaDong_ThiVanTaoDuoc()
    {
        using var t = new CampaignTestDb();
        var owner = Guid.NewGuid();
        var campaign = Windowed(owner, Open, Close);
        t.Db.Campaigns.Add(campaign);
        await t.Db.SaveChangesAsync();

        // Biên: ca bắt đầu ĐÚNG lúc chiến dịch mở và kết thúc ĐÚNG lúc đóng — hợp lệ.
        // Thiếu ca này thì đổi `<` thành `<=` (hoặc `>` thành `>=`) không test nào đỏ, mà
        // hệ quả là HR đặt ca khít mép cửa sổ lại bị từ chối không rõ lý do.
        var created = await Service(t.NewContext())
            .CreateSlotAsync(owner, campaign.Id, Create(Open, Close), default);

        Assert.Equal(2, created.Capacity);
    }

    [Fact]
    public async Task Create_CaBatDauTruocKhiChienDichMo_ThiChan()
    {
        using var t = new CampaignTestDb();
        var owner = Guid.NewGuid();
        var campaign = Windowed(owner, Open, Close);
        t.Db.Campaigns.Add(campaign);
        await t.Db.SaveChangesAsync();

        var error = await Assert.ThrowsAsync<ArgumentException>(() => Service(t.NewContext())
            .CreateSlotAsync(owner, campaign.Id, Create(Open.AddDays(-1), Open.AddHours(1)), default));

        // Thông điệp phải chỉ ra CHIẾN DỊCH, không phải chỉ nói "khung giờ không hợp lệ" —
        // đó đúng là thứ khiến HR đi tìm nguyên nhân ở chỗ khác.
        Assert.Contains("chiến dịch mở", error.Message);
    }

    [Fact]
    public async Task Create_CaKetThucSauKhiChienDichDong_ThiChan()
    {
        using var t = new CampaignTestDb();
        var owner = Guid.NewGuid();
        var campaign = Windowed(owner, Open, Close);
        t.Db.Campaigns.Add(campaign);
        await t.Db.SaveChangesAsync();

        var error = await Assert.ThrowsAsync<ArgumentException>(() => Service(t.NewContext())
            .CreateSlotAsync(owner, campaign.Id, Create(Close.AddHours(-1), Close.AddHours(2)), default));

        Assert.Contains("chiến dịch đóng", error.Message);
    }

    [Fact]
    public async Task Create_ChienDichChuaKhaiHanDong_ThiKhongRangBuocPhiaSau()
    {
        using var t = new CampaignTestDb();
        var owner = Guid.NewGuid();
        // `expires_at` null = "chưa đặt hạn đóng" ⇒ không ràng buộc phía sau. Chiến dịch đang
        // chạy mà chưa khai hạn vẫn phải tạo được ca như trước — guard mới không được phá
        // dữ liệu sẵn có.
        //
        // ⚠ Vế `starts_at = null` KHÔNG test được và cũng không xảy ra: cột đó là NOT NULL ở DB
        // (`20260713082536_InitialCreate.cs:68`) trong khi model khai `DateTime?` — model đang
        // rộng hơn schema. Nhánh null của `StartsAt` trong guard là phòng thủ, không phải đường
        // sống; giữ lại vì nó đúng nếu cột đổi sang nullable, nhưng đừng nhầm là đã được phủ.
        var campaign = Windowed(owner, Open, null);
        t.Db.Campaigns.Add(campaign);
        await t.Db.SaveChangesAsync();

        var created = await Service(t.NewContext())
            .CreateSlotAsync(owner, campaign.Id, Create(Open.AddYears(5), Open.AddYears(5).AddHours(1)), default);

        Assert.Equal(2, created.Capacity);
    }

    [Fact]
    public async Task Update_CungLuatVoiCreate_KhongChiChanOCuaTao()
    {
        using var t = new CampaignTestDb();
        var owner = Guid.NewGuid();
        var campaign = Windowed(owner, Open, Close);
        var slot = new CampaignSlot
        {
            Id = Guid.NewGuid(),
            CampaignId = campaign.Id,
            StartsAt = Open.AddDays(2),
            EndsAt = Open.AddDays(2).AddHours(1),
            Capacity = 2,
        };
        t.Db.AddRange(campaign, slot);
        await t.Db.SaveChangesAsync();

        // Nới một cửa mà quên cửa kia là lỗ đã xảy ra với `source` câu hỏi campaign (F10):
        // chặn ở Create nhưng để Update lọt thì chỉ cần sửa ca là đi vòng qua được guard.
        var error = await Assert.ThrowsAsync<ArgumentException>(() => Service(t.NewContext())
            .UpdateSlotAsync(owner, campaign.Id, slot.Id, Update(Close.AddHours(-1), Close.AddDays(3)), default));

        Assert.Contains("chiến dịch đóng", error.Message);
    }
}
