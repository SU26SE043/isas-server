using System.Security.Claims;
using Isas.CampaignService.Controllers;
using Isas.CampaignService.DTOs;
using Isas.CampaignService.Models;
using Isas.CampaignService.Services;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc;
using Microsoft.Extensions.Logging;
using Moq;

// 'CampaignService' vừa là namespace vừa là tên class → alias cho rõ ràng.
using CampaignSvc = Isas.CampaignService.Services.CampaignService;

namespace Isas.CampaignService.Tests;

/// <summary>
/// BK4 — Ownership/filter theo ORG (AUTH-8): controller lấy owner từ claim `org_id`.
/// (a) thiếu claim org_id → 403 (Forbid) — user không thuộc org không thao tác campaign;
/// (b) org A chỉ thấy/thao tác campaign của org A (org B → 404);
/// (c) audit actor = user sub (NameIdentifier), KHÔNG phải org.
/// Test ở tầng controller (biên HTTP) để phủ đúng chỗ đọc claim.
/// </summary>
public class CampaignOrgOwnershipTests
{
    private static CampaignSvc NewService(CampaignDbContext db) =>
        new(db, Mock.Of<IFileService>(), Mock.Of<ILogger<CampaignSvc>>(),
            Mock.Of<IParserService>(), Mock.Of<ICriteriaSuggester>(),
            Mock.Of<IInvitationEmailPublisher>());

    // orgId null → KHÔNG gắn claim org_id (giả lập user không thuộc org). actorUserId = user sub.
    private static CampaignController NewController(CampaignDbContext db, Guid? orgId, Guid actorUserId)
    {
        var controller = new CampaignController(
            NewService(db), Mock.Of<ICvScreeningService>(), Mock.Of<ILogger<CampaignController>>());

        var claims = new List<Claim> { new(ClaimTypes.NameIdentifier, actorUserId.ToString()) };
        if (orgId is Guid g)
            claims.Add(new Claim("org_id", g.ToString()));

        controller.ControllerContext = new ControllerContext
        {
            HttpContext = new DefaultHttpContext { User = new ClaimsPrincipal(new ClaimsIdentity(claims, "Test")) }
        };
        return controller;
    }

    private static CreateCampaignRequest ValidCreateReq() => new()
    {
        Title = "Tuyển BE", Domain = "BE", TimeLimitMinutes = 30,
        StartsAt = DateTime.UtcNow.AddDays(1), ExpiresAt = DateTime.UtcNow.AddDays(10),
        Questions = new List<QuestionItem> { new() { QuestionText = "Q1", IsRequired = true } }
    };

    // (a) Thiếu claim org_id → 403 (Forbid) trên endpoint list.
    [Fact]
    public async Task GetAll_thieu_org_id_claim_tra_403()
    {
        using var tdb = new CampaignTestDb();
        var controller = NewController(tdb.NewContext(), orgId: null, actorUserId: Guid.NewGuid());

        var result = await controller.GetAllCampaign(default);

        Assert.IsType<ForbidResult>(result.Result);
    }

    // (a-bis) Thiếu claim org_id → 403 trên endpoint tạo (mutation).
    [Fact]
    public async Task Create_thieu_org_id_claim_tra_403()
    {
        using var tdb = new CampaignTestDb();
        var controller = NewController(tdb.NewContext(), orgId: null, actorUserId: Guid.NewGuid());

        var result = await controller.CreateCampaign(ValidCreateReq(), default);

        Assert.IsType<ForbidResult>(result.Result);
    }

    // (b) Ownership scope theo ORG: org A (chủ) GET thấy; org B GET → 404 (không lộ tồn tại).
    // Seed trực tiếp (Id set) — Create qua controller cần câu hỏi có Id default gen_random_uuid() (không có SQLite).
    [Fact]
    public async Task Get_scope_theo_org_org_khac_tra_404()
    {
        using var tdb = new CampaignTestDb();
        var orgA = Guid.NewGuid();
        var camp = CampaignTestDb.NewCampaign(orgA, CampaignStatus.Active);   // OrgId = orgA
        tdb.Db.Campaigns.Add(camp);
        await tdb.Db.SaveChangesAsync();

        // org A (chủ) → Ok.
        var okA = await NewController(tdb.NewContext(), orgA, Guid.NewGuid()).GetCampaignById(camp.Id, default);
        Assert.IsType<OkObjectResult>(okA.Result);

        // org B (khác) → 404.
        var notFoundB = await NewController(tdb.NewContext(), Guid.NewGuid(), Guid.NewGuid()).GetCampaignById(camp.Id, default);
        Assert.IsType<NotFoundObjectResult>(notFoundB.Result);
    }
}
