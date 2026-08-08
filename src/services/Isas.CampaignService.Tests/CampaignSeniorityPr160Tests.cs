using Isas.CampaignService.DTOs;
using Isas.CampaignService.Models;
using Isas.CampaignService.Services;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging;
using Moq;

using CampaignSvc = Isas.CampaignService.Services.CampaignService;

namespace Isas.CampaignService.Tests;

/// <summary>
/// PR160 — hợp đồng validate `seniority` (mức kinh nghiệm cấp CHIẾN DỊCH).
///
/// Luật (giống hệt bên Interview/PracticeService — lệch là hai service từ chối khác nhau cho cùng giá trị):
///   null            → create: "Junior" · update: GIỮ NGUYÊN giá trị cũ
///   rỗng sau Trim() → 400  (KHÔNG âm thầm về "Junior")
///   ngoài tập hợp   → 400, phân biệt HOA/thường
///   hợp lệ          → giá trị đã trim
///
/// Test quan trọng nhất file này là <see cref="Update_SeniorityRong_400_KhongGhiDeMucDaChon"/>:
/// trước PR160, `"seniority": ""` ở đường UPDATE reset mức HR đã chọn về Junior mà không lỗi nào
/// phát ra — mất dữ liệu im lặng, không cách nào phát hiện từ phía HR.
/// </summary>
public class CampaignSeniorityPr160Tests
{
    private static CampaignSvc NewService(CampaignDbContext db) =>
        new(db, Mock.Of<IFileService>(), Mock.Of<ILogger<CampaignSvc>>(), Mock.Of<IParserService>(),
            Mock.Of<ICriteriaSuggester>(), Mock.Of<IInvitationEmailPublisher>(), entitlements: Entitlements());

    // Create đi qua entitlement gate (T8) → cấp grant B2B trả phí, đừng dựa vào fallback fail-closed.
    private static IEntitlementClient Entitlements()
    {
        var client = new Mock<IEntitlementClient>();
        client.Setup(x => x.ResolveOrgAsync(It.IsAny<Guid>(), It.IsAny<CancellationToken>())).ReturnsAsync(
            new CampaignEntitlement("test", "business", 1, 10, 200, true, true, true));
        return client.Object;
    }

    private static CreateCampaignRequest BaseCreate(string? seniority) => new()
    {
        Title = "Campaign",
        Domain = "BE",
        TimeLimitMinutes = 30,
        StartsAt = DateTime.UtcNow.AddMinutes(5),
        ExpiresAt = DateTime.UtcNow.AddDays(2),
        // DTO khai `string` (non-nullable) nhưng System.Text.Json vẫn gán được null khi client gửi
        // `"seniority": null` → nhánh null PHẢI xử lý được ở runtime.
        Seniority = seniority!,
    };

    // ── CREATE ─────────────────────────────────────────────────────────────────────

    // null = "không khai" → mặc định Junior (giữ hành vi cũ, không phải mọi chuỗi trống đều thế).
    [Fact]
    public async Task Create_SeniorityNull_MacDinhJunior()
    {
        using var tdb = new CampaignTestDb();
        var res = await NewService(tdb.NewContext())
            .CreateCampaignAsync(Guid.NewGuid(), Guid.NewGuid(), BaseCreate(null), default);

        Assert.Equal("Junior", res.Seniority);
    }

    // Rỗng / toàn khoảng trắng = HR GỬI một giá trị sai → 400, KHÔNG im lặng thành Junior.
    [Theory]
    [InlineData("")]
    [InlineData("   ")]
    [InlineData("\t")]
    public async Task Create_SeniorityRong_400(string seniority)
    {
        using var tdb = new CampaignTestDb();
        await Assert.ThrowsAsync<ArgumentException>(() => NewService(tdb.NewContext())
            .CreateCampaignAsync(Guid.NewGuid(), Guid.NewGuid(), BaseCreate(seniority), default));
    }

    // Case-sensitive: DB lưu đúng thang "Fresher/Junior/Middle/Senior" và CHECK ck_campaigns_seniority
    // cũng so đúng thang đó → nhận "junior" ở tầng service sẽ ghi được giá trị CHECK từ chối.
    [Theory]
    [InlineData("junior")]
    [InlineData("SENIOR")]
    [InlineData("Intern")]
    [InlineData("Lead")]
    public async Task Create_SenioritySaiThang_400(string seniority)
    {
        using var tdb = new CampaignTestDb();
        await Assert.ThrowsAsync<ArgumentException>(() => NewService(tdb.NewContext())
            .CreateCampaignAsync(Guid.NewGuid(), Guid.NewGuid(), BaseCreate(seniority), default));
    }

    // Hợp lệ nhưng dính khoảng trắng → nhận bản ĐÃ trim (không lưu " Senior " vào cột varchar(16)).
    [Fact]
    public async Task Create_SenioritySachKhoangTrang_LuuBanDaTrim()
    {
        using var tdb = new CampaignTestDb();
        var res = await NewService(tdb.NewContext())
            .CreateCampaignAsync(Guid.NewGuid(), Guid.NewGuid(), BaseCreate(" Senior "), default);

        Assert.Equal("Senior", res.Seniority);

        using var check = tdb.NewContext();
        Assert.Equal("Senior", (await check.Campaigns.SingleAsync(c => c.Id == res.Id)).Seniority);
    }

    [Theory]
    [InlineData("Fresher")]
    [InlineData("Junior")]
    [InlineData("Middle")]
    [InlineData("Senior")]
    public async Task Create_MoiMucHopLe_LuuDuoc(string seniority)
    {
        using var tdb = new CampaignTestDb();
        var res = await NewService(tdb.NewContext())
            .CreateCampaignAsync(Guid.NewGuid(), Guid.NewGuid(), BaseCreate(seniority), default);

        Assert.Equal(seniority, res.Seniority);
    }

    // ── UPDATE ─────────────────────────────────────────────────────────────────────

    private static Campaign SeededCampaign(CampaignTestDb tdb, Guid org, string seniority)
    {
        var camp = CampaignTestDb.NewCampaign(org);   // Draft — chỉ Draft mới đổi được seniority
        camp.Seniority = seniority;
        tdb.Db.Campaigns.Add(camp);
        tdb.Db.SaveChanges();
        return camp;
    }

    // 🔴 Bug PR160: `"seniority": ""` PHẢI 400 và KHÔNG được chạm vào giá trị đang lưu.
    // Trước fix: ValidateSeniority("") trả "Junior" → campaign "Senior" bị hạ về "Junior" trong im lặng.
    [Theory]
    [InlineData("")]
    [InlineData("   ")]
    public async Task Update_SeniorityRong_400_KhongGhiDeMucDaChon(string seniority)
    {
        using var tdb = new CampaignTestDb();
        var org = Guid.NewGuid();
        var camp = SeededCampaign(tdb, org, "Senior");

        await Assert.ThrowsAsync<ArgumentException>(() => NewService(tdb.NewContext())
            .UpdateCampaignAsync(org, org, camp.Id,
                new UpdateCampaignRequest { Title = "New", Seniority = seniority }, default));

        using var check = tdb.NewContext();
        Assert.Equal("Senior", (await check.Campaigns.SingleAsync(c => c.Id == camp.Id)).Seniority);
    }

    // null = KHÔNG đổi (mẫu AntiCheatEnabled/C3). Đây là vế còn lại của bug trên: null giữ nguyên,
    // rỗng thì 400 — hai ca khác nhau, không được gộp làm một.
    [Fact]
    public async Task Update_SeniorityNull_GiuNguyenGiaTriCu()
    {
        using var tdb = new CampaignTestDb();
        var org = Guid.NewGuid();
        var camp = SeededCampaign(tdb, org, "Senior");

        var res = await NewService(tdb.NewContext())
            .UpdateCampaignAsync(org, org, camp.Id,
                new UpdateCampaignRequest { Title = "New", Seniority = null }, default);

        Assert.Equal("Senior", res.Seniority);
        using var check = tdb.NewContext();
        Assert.Equal("Senior", (await check.Campaigns.SingleAsync(c => c.Id == camp.Id)).Seniority);
    }

    [Fact]
    public async Task Update_SenioritySaiThang_400_KhongGhiDe()
    {
        using var tdb = new CampaignTestDb();
        var org = Guid.NewGuid();
        var camp = SeededCampaign(tdb, org, "Senior");

        await Assert.ThrowsAsync<ArgumentException>(() => NewService(tdb.NewContext())
            .UpdateCampaignAsync(org, org, camp.Id,
                new UpdateCampaignRequest { Title = "New", Seniority = "senior" }, default));

        using var check = tdb.NewContext();
        Assert.Equal("Senior", (await check.Campaigns.SingleAsync(c => c.Id == camp.Id)).Seniority);
    }

    [Fact]
    public async Task Update_SenioritySachKhoangTrang_LuuBanDaTrim()
    {
        using var tdb = new CampaignTestDb();
        var org = Guid.NewGuid();
        var camp = SeededCampaign(tdb, org, "Junior");

        var res = await NewService(tdb.NewContext())
            .UpdateCampaignAsync(org, org, camp.Id,
                new UpdateCampaignRequest { Title = "New", Seniority = " Middle " }, default);

        Assert.Equal("Middle", res.Seniority);
        using var check = tdb.NewContext();
        Assert.Equal("Middle", (await check.Campaigns.SingleAsync(c => c.Id == camp.Id)).Seniority);
    }
}
