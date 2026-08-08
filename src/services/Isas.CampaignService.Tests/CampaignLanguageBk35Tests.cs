using Isas.CampaignService.DTOs;
using Isas.CampaignService.Models;
using Isas.CampaignService.Services;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Logging;
using Moq;

using CampaignSvc = Isas.CampaignService.Services.CampaignService;

namespace Isas.CampaignService.Tests;

/// <summary>
/// BK35 — hợp đồng validate <c>language</c> (ngôn ngữ cấp CHIẾN DỊCH).
///
/// Luật (cùng hình dạng với <see cref="CampaignSeniorityPr160Tests"/> sau PR160):
///   null            → create: "vi" · update: GIỮ NGUYÊN giá trị cũ
///   rỗng sau Trim() → 400  (KHÔNG âm thầm về "vi")
///   ngoài {vi,en}   → 400
///   bilingual tắt   → xin "en" là 400
///   hợp lệ          → giá trị đã trim + hạ về CHỮ THƯỜNG
///
/// ⚠ KHÁC <c>seniority</c> đúng một điểm và cố ý giữ khác: <c>language</c> chuẩn hoá về chữ thường
/// (<c>"EN"</c> → <c>"en"</c>) vì cột DB và hợp đồng liên service đều dùng chữ thường, còn
/// <c>seniority</c> thì phân biệt HOA/thường. Đừng "cho nhất quán" mà đổi một trong hai.
///
/// Test quan trọng nhất file này là <see cref="Update_LanguageRong_400_KhongGhiDeNgonNguDaChon"/>:
/// trước BK35, <c>"language": ""</c> ở đường UPDATE hạ campaign <c>en</c> về <c>vi</c> mà không lỗi
/// nào phát ra — mất dữ liệu im lặng, HR không có cách nào phát hiện. Nó assert CẢ HAI vế
/// (ném 400 <b>và</b> giá trị trong DB không đổi); chỉ assert 400 thì chưa chứng minh dữ liệu
/// còn nguyên.
/// </summary>
public class CampaignLanguageBk35Tests
{
    // Bilingual mặc định TẮT trong service (config vắng → false), nên test nào cần "en" phải bật
    // tường minh — chính thứ tự đó là điều `BilingualTat_XinEn_400` khoá lại.
    private static IConfiguration Config(bool bilingual) =>
        new ConfigurationBuilder().AddInMemoryCollection(new Dictionary<string, string?>
        {
            ["Campaign:Bilingual:Enabled"] = bilingual ? "true" : "false"
        }).Build();

    private static CampaignSvc NewService(CampaignDbContext db, bool bilingual = true) =>
        new(db, Mock.Of<IFileService>(), Mock.Of<ILogger<CampaignSvc>>(), Mock.Of<IParserService>(),
            Mock.Of<ICriteriaSuggester>(), Mock.Of<IInvitationEmailPublisher>(),
            entitlements: Entitlements(), config: Config(bilingual));

    // Create đi qua entitlement gate (T8) → cấp grant B2B trả phí, đừng dựa vào fallback fail-closed.
    private static IEntitlementClient Entitlements()
    {
        var client = new Mock<IEntitlementClient>();
        client.Setup(x => x.ResolveOrgAsync(It.IsAny<Guid>(), It.IsAny<CancellationToken>())).ReturnsAsync(
            new CampaignEntitlement("test", "business", 1, 10, 200, true, true, true));
        return client.Object;
    }

    private static CreateCampaignRequest BaseCreate(string? language) => new()
    {
        Title = "Campaign",
        Domain = "BE",
        TimeLimitMinutes = 30,
        StartsAt = DateTime.UtcNow.AddMinutes(5),
        ExpiresAt = DateTime.UtcNow.AddDays(2),
        Language = language,
    };

    // ── CREATE ─────────────────────────────────────────────────────────────────────

    // null = "không khai" → mặc định vi (giữ hành vi cũ; KHÔNG phải mọi chuỗi trống đều thế).
    [Fact]
    public async Task Create_LanguageNull_MacDinhVi()
    {
        using var tdb = new CampaignTestDb();
        var res = await NewService(tdb.NewContext())
            .CreateCampaignAsync(Guid.NewGuid(), Guid.NewGuid(), BaseCreate(null), default);

        Assert.Equal("vi", res.Language);

        using var check = tdb.NewContext();
        Assert.Equal("vi", (await check.Campaigns.SingleAsync(c => c.Id == res.Id)).Language);
    }

    // Rỗng / toàn khoảng trắng = HR GỬI một giá trị sai → 400, KHÔNG im lặng thành "vi".
    [Theory]
    [InlineData("")]
    [InlineData("   ")]
    [InlineData("\t")]
    public async Task Create_LanguageRong_400(string language)
    {
        using var tdb = new CampaignTestDb();
        await Assert.ThrowsAsync<ArgumentException>(() => NewService(tdb.NewContext())
            .CreateCampaignAsync(Guid.NewGuid(), Guid.NewGuid(), BaseCreate(language), default));
    }

    // Chuẩn hoá về CHỮ THƯỜNG + đã trim: cột DB và hợp đồng liên service (Interview/AIService) đều
    // dùng "vi"/"en" chữ thường; lưu " EN " sẽ làm mọi phép so ngôn ngữ phía sau trượt.
    [Theory]
    [InlineData("EN", "en")]
    [InlineData(" En ", "en")]
    [InlineData("VI", "vi")]
    [InlineData(" vi\t", "vi")]
    public async Task Create_LanguageChuanHoaChuThuongVaTrim(string requested, string expected)
    {
        using var tdb = new CampaignTestDb();
        var res = await NewService(tdb.NewContext())
            .CreateCampaignAsync(Guid.NewGuid(), Guid.NewGuid(), BaseCreate(requested), default);

        Assert.Equal(expected, res.Language);

        using var check = tdb.NewContext();
        Assert.Equal(expected, (await check.Campaigns.SingleAsync(c => c.Id == res.Id)).Language);
    }

    [Theory]
    [InlineData("fr")]
    [InlineData("jp")]
    [InlineData("vie")]
    [InlineData("english")]
    public async Task Create_LanguageSaiThang_400(string language)
    {
        using var tdb = new CampaignTestDb();
        await Assert.ThrowsAsync<ArgumentException>(() => NewService(tdb.NewContext())
            .CreateCampaignAsync(Guid.NewGuid(), Guid.NewGuid(), BaseCreate(language), default));
    }

    // Cờ song ngữ TẮT (mặc định prod) → chỉ "vi" đi lọt; xin "en" là 400, không hồi quy.
    [Fact]
    public async Task Create_BilingualTat_XinEn_400()
    {
        using var tdb = new CampaignTestDb();
        await Assert.ThrowsAsync<ArgumentException>(() => NewService(tdb.NewContext(), bilingual: false)
            .CreateCampaignAsync(Guid.NewGuid(), Guid.NewGuid(), BaseCreate("en"), default));
    }

    [Fact]
    public async Task Create_BilingualTat_XinVi_VanTao()
    {
        using var tdb = new CampaignTestDb();
        var res = await NewService(tdb.NewContext(), bilingual: false)
            .CreateCampaignAsync(Guid.NewGuid(), Guid.NewGuid(), BaseCreate("vi"), default);

        Assert.Equal("vi", res.Language);
    }

    // ── UPDATE ─────────────────────────────────────────────────────────────────────

    private static Campaign SeededCampaign(CampaignTestDb tdb, Guid org, string language)
    {
        var camp = CampaignTestDb.NewCampaign(org);   // Draft — chỉ Draft mới đổi được language
        camp.Language = language;
        tdb.Db.Campaigns.Add(camp);
        tdb.Db.SaveChanges();
        return camp;
    }

    // 🔴 Bug BK35: `"language": ""` PHẢI 400 và KHÔNG được chạm vào giá trị đang lưu.
    // Trước fix: ValidateLanguage("") trả "vi" → campaign "en" bị hạ về "vi" trong im lặng.
    [Theory]
    [InlineData("")]
    [InlineData("   ")]
    [InlineData("\t")]
    public async Task Update_LanguageRong_400_KhongGhiDeNgonNguDaChon(string language)
    {
        using var tdb = new CampaignTestDb();
        var org = Guid.NewGuid();
        var camp = SeededCampaign(tdb, org, "en");

        await Assert.ThrowsAsync<ArgumentException>(() => NewService(tdb.NewContext())
            .UpdateCampaignAsync(org, org, camp.Id,
                new UpdateCampaignRequest { Title = "New", Language = language }, default));

        using var check = tdb.NewContext();
        Assert.Equal("en", (await check.Campaigns.SingleAsync(c => c.Id == camp.Id)).Language);
    }

    // null = KHÔNG đổi (mẫu AntiCheatEnabled/C3). Vế còn lại của bug trên: null giữ nguyên,
    // rỗng thì 400 — hai ca khác nhau, không được gộp làm một.
    [Fact]
    public async Task Update_LanguageNull_GiuNguyenGiaTriCu()
    {
        using var tdb = new CampaignTestDb();
        var org = Guid.NewGuid();
        var camp = SeededCampaign(tdb, org, "en");

        var res = await NewService(tdb.NewContext())
            .UpdateCampaignAsync(org, org, camp.Id,
                new UpdateCampaignRequest { Title = "New", Language = null }, default);

        Assert.Equal("en", res.Language);
        using var check = tdb.NewContext();
        Assert.Equal("en", (await check.Campaigns.SingleAsync(c => c.Id == camp.Id)).Language);
    }

    [Theory]
    [InlineData("fr")]
    [InlineData("english")]
    public async Task Update_LanguageSaiThang_400_KhongGhiDe(string language)
    {
        using var tdb = new CampaignTestDb();
        var org = Guid.NewGuid();
        var camp = SeededCampaign(tdb, org, "en");

        await Assert.ThrowsAsync<ArgumentException>(() => NewService(tdb.NewContext())
            .UpdateCampaignAsync(org, org, camp.Id,
                new UpdateCampaignRequest { Title = "New", Language = language }, default));

        using var check = tdb.NewContext();
        Assert.Equal("en", (await check.Campaigns.SingleAsync(c => c.Id == camp.Id)).Language);
    }

    [Fact]
    public async Task Update_LanguageHopLe_LuuBanChuThuongDaTrim()
    {
        using var tdb = new CampaignTestDb();
        var org = Guid.NewGuid();
        var camp = SeededCampaign(tdb, org, "vi");

        var res = await NewService(tdb.NewContext())
            .UpdateCampaignAsync(org, org, camp.Id,
                new UpdateCampaignRequest { Title = "New", Language = " EN " }, default);

        Assert.Equal("en", res.Language);
        using var check = tdb.NewContext();
        Assert.Equal("en", (await check.Campaigns.SingleAsync(c => c.Id == camp.Id)).Language);
    }

    // Cờ song ngữ TẮT → không nâng cấp được campaign vi lên en, và giá trị cũ còn nguyên.
    [Fact]
    public async Task Update_BilingualTat_XinEn_400_KhongGhiDe()
    {
        using var tdb = new CampaignTestDb();
        var org = Guid.NewGuid();
        var camp = SeededCampaign(tdb, org, "vi");

        await Assert.ThrowsAsync<ArgumentException>(() => NewService(tdb.NewContext(), bilingual: false)
            .UpdateCampaignAsync(org, org, camp.Id,
                new UpdateCampaignRequest { Title = "New", Language = "en" }, default));

        using var check = tdb.NewContext();
        Assert.Equal("vi", (await check.Campaigns.SingleAsync(c => c.Id == camp.Id)).Language);
    }
}
