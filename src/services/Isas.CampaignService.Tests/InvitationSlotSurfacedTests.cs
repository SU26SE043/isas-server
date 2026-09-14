using Isas.CampaignService.Models;
using Isas.CampaignService.Services;
using Microsoft.Extensions.Logging;
using Moq;
using CampaignSvc = Isas.CampaignService.Services.CampaignService;

namespace Isas.CampaignService.Tests;

/// <summary>
/// Danh sách lời mời phải nói ca thi đã gán.
///
/// <para>Hệ thống gán ứng viên vào ca (<c>AssignSlotsAsync</c>) và THƯ MỜI có nói giờ ca
/// (<c>InvitationEmailConsumer</c> tra ca rồi truyền vào <c>CampaignEmailSender</c>), nhưng
/// <c>InvitationListItem</c> lại không có trường nào về ca ⇒ HR chỉ thấy mỗi ca còn bao nhiêu chỗ,
/// không biết AI ở ca nào. Ứng viên báo bận giờ đó là HR không tra được, cũng không đổi được.</para>
/// </summary>
public class InvitationSlotSurfacedTests
{
    private static CampaignSvc Service(CampaignDbContext db) => new(db, Mock.Of<IFileService>(),
        Mock.Of<ILogger<CampaignSvc>>(), Mock.Of<IParserService>(), Mock.Of<ICriteriaSuggester>(), Mock.Of<IInvitationEmailPublisher>());

    private static CampaignInvitation Invite(Guid campaignId, string email, Guid? slotId) => new()
    {
        Id = Guid.NewGuid(),
        CampaignId = campaignId,
        SlotId = slotId,
        Email = email,
        TokenHash = Guid.NewGuid().ToString("N"),
        ExpiresAt = DateTime.UtcNow.AddDays(5),
        CreatedAt = DateTime.UtcNow,
    };

    [Fact]
    public async Task DanhSachLoiMoi_NoiRoCaThiDaGan()
    {
        using var t = new CampaignTestDb();
        var owner = Guid.NewGuid();
        var campaign = CampaignTestDb.NewCampaign(owner);
        var starts = DateTime.UtcNow.AddDays(1);
        var slot = new CampaignSlot
        {
            Id = Guid.NewGuid(), CampaignId = campaign.Id,
            StartsAt = starts, EndsAt = starts.AddHours(2), Capacity = 5,
        };
        t.Db.AddRange(campaign, slot,
            Invite(campaign.Id, "co-ca@x.test", slot.Id),
            Invite(campaign.Id, "khong-ca@x.test", null));
        await t.Db.SaveChangesAsync();

        var page = await Service(t.NewContext()).GetInvitationsAsync(owner, campaign.Id, null, null, null, null, default);
        var withSlot = page.Items.Single(i => i.Email == "co-ca@x.test");
        var without = page.Items.Single(i => i.Email == "khong-ca@x.test");

        Assert.Equal(slot.Id, withSlot.SlotId);
        Assert.Equal(slot.StartsAt, withSlot.SlotStartsAt);
        Assert.Equal(slot.EndsAt, withSlot.SlotEndsAt);

        // Chiến dịch không có ca là trạng thái BÌNH THƯỜNG (ca thi tuỳ chọn) — null ở đây nghĩa là
        // "thi bất kỳ lúc nào trong cửa sổ", không phải thiếu dữ liệu.
        Assert.Null(without.SlotId);
        Assert.Null(without.SlotStartsAt);
        Assert.Null(without.SlotEndsAt);
    }

    [Fact]
    public async Task CaCuaChienDichKHAC_KhongRoRiSangDanhSachNay()
    {
        using var t = new CampaignTestDb();
        var owner = Guid.NewGuid();
        var mine = CampaignTestDb.NewCampaign(owner);
        var other = CampaignTestDb.NewCampaign(owner);
        var starts = DateTime.UtcNow.AddDays(1);
        // Ca thuộc chiến dịch KHÁC. Nạp ca theo campaign (một lượt) nên vị ngữ `CampaignId` là thứ
        // duy nhất chặn việc tra nhầm sang ca của chiến dịch khác.
        var alien = new CampaignSlot
        {
            Id = Guid.NewGuid(), CampaignId = other.Id,
            StartsAt = starts, EndsAt = starts.AddHours(1), Capacity = 3,
        };
        t.Db.AddRange(mine, other, alien, Invite(mine.Id, "a@x.test", alien.Id));
        await t.Db.SaveChangesAsync();

        var page = await Service(t.NewContext()).GetInvitationsAsync(owner, mine.Id, null, null, null, null, default);
        var item = page.Items.Single();

        Assert.Equal(alien.Id, item.SlotId);          // id vẫn echo nguyên (sự thật trong DB)
        Assert.Null(item.SlotStartsAt);               // nhưng KHÔNG lấy giờ của ca chiến dịch khác
        Assert.Null(item.SlotEndsAt);
    }
}
