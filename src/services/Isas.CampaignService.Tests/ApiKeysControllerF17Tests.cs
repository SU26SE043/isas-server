using Isas.CampaignService.Controllers;
using Isas.CampaignService.DTOs;
using Isas.CampaignService.Models;
using Isas.CampaignService.Services;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using Moq;
using System.Security.Claims;

namespace Isas.CampaignService.Tests;

/// <summary>
/// F17 — gate quản lý key: chỉ **OrgAdmin** (AUTH-4). HrMember / không thuộc org → 403.
/// </summary>
public class ApiKeysControllerF17Tests
{
    private static ApiKeysController NewController(
        CampaignDbContext db, Guid? orgId, string? orgRole)
    {
        var svc = new ApiKeyService(
            db, Options.Create(new ApiKeySettings()), Mock.Of<ILogger<ApiKeyService>>());
        var controller = new ApiKeysController(svc, Mock.Of<ILogger<ApiKeysController>>());

        var claims = new List<Claim> { new(ClaimTypes.NameIdentifier, Guid.NewGuid().ToString()) };
        if (orgId is not null) claims.Add(new Claim("org_id", orgId.Value.ToString()));
        if (orgRole is not null) claims.Add(new Claim("org_role", orgRole));

        controller.ControllerContext = new ControllerContext
        {
            HttpContext = new DefaultHttpContext
            {
                User = new ClaimsPrincipal(new ClaimsIdentity(claims, "Test"))
            }
        };
        return controller;
    }

    private static CreateApiKeyRequest Req() => new() { Name = "Greenhouse" };

    [Fact]
    public async Task OrgAdmin_tao_duoc_key_va_nhan_key_tho_mot_lan()
    {
        using var tdb = new CampaignTestDb();
        var orgId = Guid.NewGuid();

        var result = await NewController(tdb.NewContext(), orgId, "OrgAdmin").CreateApiKey(Req(), default);

        var created = Assert.IsType<CreatedApiKeyResponse>(
            Assert.IsType<ObjectResult>(result.Result).Value);
        Assert.StartsWith(ApiKeys.Prefix, created.Key);
    }

    [Theory]
    [InlineData("HrMember")]   // AUTH-4: HR quản campaign, KHÔNG uỷ quyền truy cập dữ liệu org
    [InlineData(null)]         // không thuộc org
    public async Task Khong_phai_OrgAdmin_thi_403_o_moi_endpoint(string? orgRole)
    {
        using var tdb = new CampaignTestDb();
        var orgId = Guid.NewGuid();

        Assert.IsType<ForbidResult>(
            (await NewController(tdb.NewContext(), orgId, orgRole).CreateApiKey(Req(), default)).Result);
        Assert.IsType<ForbidResult>(
            (await NewController(tdb.NewContext(), orgId, orgRole).ListApiKeys(default)).Result);
        Assert.IsType<ForbidResult>(
            await NewController(tdb.NewContext(), orgId, orgRole).RevokeApiKey(Guid.NewGuid(), default));

        // Và không có key nào được tạo ra.
        Assert.Empty(tdb.NewContext().ApiKeys.ToList());
    }

    [Fact]
    public async Task Khong_co_claim_org_thi_403_du_la_OrgAdmin()
    {
        using var tdb = new CampaignTestDb();

        Assert.IsType<ForbidResult>(
            (await NewController(tdb.NewContext(), null, "OrgAdmin").CreateApiKey(Req(), default)).Result);
    }

    [Fact]
    public async Task Revoke_key_org_khac_tra_404()
    {
        using var tdb = new CampaignTestDb();
        var orgA = Guid.NewGuid();
        var orgB = Guid.NewGuid();

        var createdB = Assert.IsType<CreatedApiKeyResponse>(Assert.IsType<ObjectResult>(
            (await NewController(tdb.NewContext(), orgB, "OrgAdmin").CreateApiKey(Req(), default)).Result).Value);

        var result = await NewController(tdb.NewContext(), orgA, "OrgAdmin")
            .RevokeApiKey(createdB.Id, default);

        Assert.IsType<NotFoundObjectResult>(result);
        Assert.Null(tdb.NewContext().ApiKeys.Single().RevokedAt);   // key B chưa bị đụng
    }

    [Fact]
    public async Task OrgAdmin_thu_hoi_duoc_key_cua_org_minh()
    {
        using var tdb = new CampaignTestDb();
        var orgId = Guid.NewGuid();

        var created = Assert.IsType<CreatedApiKeyResponse>(Assert.IsType<ObjectResult>(
            (await NewController(tdb.NewContext(), orgId, "OrgAdmin").CreateApiKey(Req(), default)).Result).Value);

        var result = await NewController(tdb.NewContext(), orgId, "OrgAdmin")
            .RevokeApiKey(created.Id, default);

        Assert.IsType<NoContentResult>(result);
        Assert.NotNull(tdb.NewContext().ApiKeys.Single().RevokedAt);
    }
}
