using System.Security.Claims;
using Isas.CampaignService.Controllers;
using Isas.CampaignService.DTOs;
using Isas.CampaignService.Models;
using Isas.CampaignService.Services;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging;
using Moq;
using Xunit;

using CampaignSvc = Isas.CampaignService.Services.CampaignService;

namespace Isas.CampaignService.Tests;

/// <summary>
/// POST /campaign KHÔNG còn chặn giờ mở ở quá khứ (chốt duy nhất trong hệ nói khác với PUT và
/// start-now — xem chú thích tại <see cref="CampaignController.CreateCampaign"/>). Đo trên prod
/// 21/09: FE tạo nháp lười ở bước 2 với giờ mở = lúc mở wizard + 1', HR điền bước 1 quá 1' ⇒ 400
/// "StartsAt cannot be in the past." ⇒ tải JD báo "không kết nối được máy chủ".
///
/// <para>Chốt bị gỡ NẰM Ở CONTROLLER ⇒ test chạm lớp controller trực tiếp. Hai chốt còn lại
/// (hết hạn ở quá khứ · mở ≥ hết hạn) phải VẪN 400 — gỡ nhầm cả ba là mở cửa cho dữ liệu vô nghĩa.</para>
/// </summary>
public class CampaignCreatePastStartTests
{
    private static IEntitlementClient Entitlements()
    {
        var m = new Mock<IEntitlementClient>();
        m.Setup(x => x.ResolveOrgAsync(It.IsAny<Guid>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(new CampaignEntitlement("test", "business", 5, 10, 200, true, true, true));
        return m.Object;
    }

    private static CampaignController NewController(CampaignDbContext db, Guid orgId)
    {
        var svc = new CampaignSvc(db, Mock.Of<IFileService>(), Mock.Of<ILogger<CampaignSvc>>(), Mock.Of<IParserService>(),
            Mock.Of<ICriteriaSuggester>(), Mock.Of<IInvitationEmailPublisher>(), entitlements: Entitlements());
        var c = new CampaignController(svc, Mock.Of<ICvScreeningService>(), Mock.Of<ILogger<CampaignController>>());
        c.ControllerContext = new ControllerContext
        {
            HttpContext = new DefaultHttpContext
            {
                User = new ClaimsPrincipal(new ClaimsIdentity(new[]
                {
                    new Claim(ClaimTypes.NameIdentifier, Guid.NewGuid().ToString()),
                    new Claim("org_id", orgId.ToString()),
                    new Claim("org_role", "OrgAdmin"),
                }, "Test")),
            },
        };
        return c;
    }

    private static CreateCampaignRequest Req(DateTime startsAt, DateTime expiresAt) => new()
    {
        Title = "Past-start draft",
        Domain = "BE",
        TimeLimitMinutes = 30,
        StartsAt = startsAt,
        ExpiresAt = expiresAt,
        Questions = new List<QuestionItem>(),
    };

    // ── 1. Giờ mở đã qua 10 phút ⇒ 200, nháp tạo được, StartsAt lưu NGUYÊN (không bị kéo về now) ──
    [Fact]
    public async Task CreateCampaign_StartsAtQuaKhu_200_LuuNguyenGioMo()
    {
        using var tdb = new CampaignTestDb();
        var org = Guid.NewGuid();
        var controller = NewController(tdb.NewContext(), org);
        var startsAt = DateTime.UtcNow.AddMinutes(-10);

        var result = await controller.CreateCampaign(Req(startsAt, DateTime.UtcNow.AddDays(7)), default);

        var ok = Assert.IsType<OkObjectResult>(result.Result);
        var body = Assert.IsType<CampaignResponse>(ok.Value);
        Assert.Equal("Draft", body.Status);

        var saved = await tdb.NewContext().Campaigns.SingleAsync(c => c.Id == body.Id);
        Assert.NotNull(saved.StartsAt);
        Assert.Equal(startsAt, saved.StartsAt!.Value, TimeSpan.FromSeconds(1));
    }

    // ── 2. Hết hạn ở quá khứ ⇒ VẪN 400 (chốt này không bị gỡ nhầm theo) ─────────────────────────
    [Fact]
    public async Task CreateCampaign_ExpiresAtQuaKhu_Van400()
    {
        using var tdb = new CampaignTestDb();
        var controller = NewController(tdb.NewContext(), Guid.NewGuid());

        var result = await controller.CreateCampaign(
            Req(DateTime.UtcNow.AddDays(-3), DateTime.UtcNow.AddMinutes(-1)), default);

        var bad = Assert.IsType<BadRequestObjectResult>(result.Result);
        Assert.Contains("ExpiresAt cannot be in the past", bad.Value!.ToString());
        Assert.Empty(await tdb.NewContext().Campaigns.ToListAsync());
    }

    // ── 3. Mở ≥ hết hạn ⇒ VẪN 400 ────────────────────────────────────────────────────────────────
    [Fact]
    public async Task CreateCampaign_StartsAtSauExpiresAt_Van400()
    {
        using var tdb = new CampaignTestDb();
        var controller = NewController(tdb.NewContext(), Guid.NewGuid());

        var result = await controller.CreateCampaign(
            Req(DateTime.UtcNow.AddDays(10), DateTime.UtcNow.AddDays(7)), default);

        var bad = Assert.IsType<BadRequestObjectResult>(result.Result);
        Assert.Contains("StartsAt must be before ExpiresAt", bad.Value!.ToString());
        Assert.Empty(await tdb.NewContext().Campaigns.ToListAsync());
    }
}
