using Isas.CampaignService.DTOs;
using Isas.CampaignService.Models;
using Isas.CampaignService.Services;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging;
using Moq;

using CampaignSvc = Isas.CampaignService.Services.CampaignService;

namespace Isas.CampaignService.Tests;

/// <summary>
/// Nối dây `max_concurrent_interviews` từ API xuống DB — và ngược lại.
///
/// 🔴 Vì sao có file này: PR #138 thêm cột, thêm guard đọc nó ở ParticipationService, nhưng KHÔNG
/// field nào trong CreateCampaignRequest/UpdateCampaignRequest/CampaignResponse. Kết quả là trần
/// per-campaign ship ra ở trạng thái CHẾT: `PUT` trả 200 (JSON thừa bị bỏ qua im lặng), DB vẫn NULL,
/// HR không bao giờ thấy giá trị. E2E trên production bắt được, không test nào bắt.
///
/// Lọt vì mọi test cũ gán THẲNG `campaign.MaxConcurrentInterviews` lên entity, bỏ qua tầng DTO —
/// đúng khoảng mù mà bộ test này lấp: mọi assert dưới đây đi qua Create/UpdateCampaignAsync.
/// </summary>
public class CampaignConcurrencyCapWiringTests
{
    private static CampaignSvc NewSvc(CampaignDbContext db) =>
        new(db, Mock.Of<IFileService>(), Mock.Of<ILogger<CampaignSvc>>(), Mock.Of<IParserService>(),
            Mock.Of<ICriteriaSuggester>(), Mock.Of<IInvitationEmailPublisher>(), entitlements: Entitlements());

    private static IEntitlementClient Entitlements()
    {
        var client = new Mock<IEntitlementClient>();
        client.Setup(x => x.ResolveOrgAsync(It.IsAny<Guid>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(new CampaignEntitlement("test", "business", 1, 10, 200, true, true, true));
        return client.Object;
    }

    private static CreateCampaignRequest NewRequest(int? cap) => new()
    {
        Title = "Chiến dịch thử",
        // campaigns.starts_at là NOT NULL (CampaignDbContext.cs:71) — thiếu là vỡ lúc INSERT.
        StartsAt = DateTime.UtcNow.AddMinutes(5),
        ExpiresAt = DateTime.UtcNow.AddDays(7),
        Questions = new List<QuestionItem> { new() { QuestionText = "Câu 1", IsRequired = true } },
        MaxConcurrentInterviews = cap
    };

    [Fact]
    public async Task Create_LuuTranXuongDB_VaTraVeTrongResponse()
    {
        using var tdb = new CampaignTestDb();
        var org = Guid.NewGuid();

        var res = await NewSvc(tdb.NewContext()).CreateCampaignAsync(org, org, NewRequest(3), default);

        // Vế 1 — response phải mang field (thiếu vế này thì HR không bao giờ đọc được trần đã đặt).
        Assert.Equal(3, res.MaxConcurrentInterviews);

        // Vế 2 — và nó phải THẬT SỰ nằm dưới DB. Chỉ assert response là chưa đủ: đúng lỗi gốc là
        // giá trị đi lạc giữa DTO và entity mà HTTP vẫn trả 200.
        using var check = tdb.NewContext();
        Assert.Equal(3, (await check.Campaigns.SingleAsync()).MaxConcurrentInterviews);
    }

    [Fact]
    public async Task Update_LuuTranXuongDB()
    {
        using var tdb = new CampaignTestDb();
        var org = Guid.NewGuid();
        var camp = CampaignTestDb.NewCampaign(org);
        tdb.Db.Campaigns.Add(camp);
        await tdb.Db.SaveChangesAsync();

        var res = await NewSvc(tdb.NewContext()).UpdateCampaignAsync(org, org, camp.Id,
            new UpdateCampaignRequest { Title = "x", MaxConcurrentInterviews = 5 }, default);

        Assert.Equal(5, res.MaxConcurrentInterviews);
        using var check = tdb.NewContext();
        Assert.Equal(5, (await check.Campaigns.SingleAsync()).MaxConcurrentInterviews);
    }

    [Fact]
    public async Task Update_TranNull_GiuGiaTriCu()
    {
        using var tdb = new CampaignTestDb();
        var org = Guid.NewGuid();
        var camp = CampaignTestDb.NewCampaign(org);
        camp.MaxConcurrentInterviews = 7;
        tdb.Db.Campaigns.Add(camp);
        await tdb.Db.SaveChangesAsync();

        var res = await NewSvc(tdb.NewContext()).UpdateCampaignAsync(org, org, camp.Id,
            new UpdateCampaignRequest { Title = "x", MaxConcurrentInterviews = null }, default);

        Assert.Equal(7, res.MaxConcurrentInterviews);   // null = KHÔNG đổi, đồng nếp các trần khác
    }

    [Fact]
    public async Task KhongDatTran_ThiLaNull_KhongGioiHan()
    {
        using var tdb = new CampaignTestDb();
        var org = Guid.NewGuid();

        var res = await NewSvc(tdb.NewContext()).CreateCampaignAsync(org, org, NewRequest(null), default);

        Assert.Null(res.MaxConcurrentInterviews);
    }

    // 🔴 0 và số âm phải bị CHẶN Ở ĐÂY, nơi HR nhập. Guard bên ParticipationService là
    // `running >= max`, nên `0 >= 0` và `0 >= -1` đều đúng ngay từ ứng viên ĐẦU TIÊN ⇒ mọi lượt
    // Start trả 429 và chiến dịch khoá vĩnh viễn mà không ai hiểu vì sao. Đúng bài học F2b.
    [Theory]
    [InlineData(0)]
    [InlineData(-1)]
    [InlineData(-100)]
    public async Task Create_TranNhoHon1_Bi400(int cap)
    {
        using var tdb = new CampaignTestDb();
        var org = Guid.NewGuid();

        var ex = await Assert.ThrowsAsync<ArgumentException>(() =>
            NewSvc(tdb.NewContext()).CreateCampaignAsync(org, org, NewRequest(cap), default));

        Assert.Contains(">= 1", ex.Message);
    }

    [Theory]
    [InlineData(0)]
    [InlineData(-1)]
    public async Task Update_TranNhoHon1_Bi400(int cap)
    {
        using var tdb = new CampaignTestDb();
        var org = Guid.NewGuid();
        var camp = CampaignTestDb.NewCampaign(org);
        tdb.Db.Campaigns.Add(camp);
        await tdb.Db.SaveChangesAsync();

        await Assert.ThrowsAsync<ArgumentException>(() =>
            NewSvc(tdb.NewContext()).UpdateCampaignAsync(org, org, camp.Id,
                new UpdateCampaignRequest { Title = "x", MaxConcurrentInterviews = cap }, default));

        // Và KHÔNG được ghi nửa vời: campaign phải còn nguyên trần cũ (null).
        using var check = tdb.NewContext();
        Assert.Null((await check.Campaigns.SingleAsync()).MaxConcurrentInterviews);
    }

    [Fact]
    public async Task Tran1_LaHopLe_KhongBiChanNham()
    {
        using var tdb = new CampaignTestDb();
        var org = Guid.NewGuid();

        var res = await NewSvc(tdb.NewContext()).CreateCampaignAsync(org, org, NewRequest(1), default);

        Assert.Equal(1, res.MaxConcurrentInterviews);   // biên dưới hợp lệ — "mỗi lần 1 người"
    }
}
