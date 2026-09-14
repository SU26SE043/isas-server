using Isas.AuthService.Controllers;
using Isas.AuthService.DTOs;
using Isas.AuthService.Services;
using Microsoft.AspNetCore.Mvc;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Logging.Abstractions;
using Moq;

namespace Isas.AuthService.Tests;

/// <summary>
/// CMP1-B1 — GET /internal/auth/organizations/{orgId} (máy-máy, X-Internal-Token). CampaignService
/// gọi để điền <c>orgName</c> trên trang lời mời; Campaign chỉ giữ org_id (GEN-2). Thin wrapper quanh
/// <see cref="IAuthService.GetOrganizationAsync"/> — test ở tầng controller (gate token + shape + 404).
/// </summary>
public class InternalOrgLookupCmp1B1Tests
{
    private const string Token = "test-internal-token";

    private static IConfiguration Config() =>
        new ConfigurationBuilder().AddInMemoryCollection(new Dictionary<string, string?>
        {
            ["Internal:Token"] = Token
        }).Build();

    private static InternalAuthController NewController(Mock<IAuthService> auth) =>
        new(auth.Object, Config(), NullLogger<InternalAuthController>.Instance);

    [Fact]
    public async Task SaiInternalToken_Tra401()
    {
        var result = await NewController(new Mock<IAuthService>())
            .GetOrganization(Guid.NewGuid(), token: "wrong", default);

        Assert.IsType<UnauthorizedObjectResult>(result.Result);
    }

    [Fact]
    public async Task TokenDung_OrgTonTai_TraIdVaName()
    {
        var orgId = Guid.NewGuid();
        var auth = new Mock<IAuthService>();
        auth.Setup(x => x.GetOrganizationAsync(orgId, It.IsAny<CancellationToken>()))
            .ReturnsAsync(new OrganizationResponse { Id = orgId, Name = "Công ty Acme", MemberCount = 3 });

        var result = await NewController(auth).GetOrganization(orgId, token: Token, default);

        var ok = Assert.IsType<OkObjectResult>(result.Result);
        var body = Assert.IsType<InternalOrganizationResponse>(ok.Value);
        Assert.Equal(orgId, body.Id);
        Assert.Equal("Công ty Acme", body.Name);
    }

    [Fact]
    public async Task OrgKhongTonTai_Tra404()
    {
        var auth = new Mock<IAuthService>();
        auth.Setup(x => x.GetOrganizationAsync(It.IsAny<Guid>(), It.IsAny<CancellationToken>()))
            .ThrowsAsync(new KeyNotFoundException("Tổ chức không tồn tại."));

        var result = await NewController(auth).GetOrganization(Guid.NewGuid(), token: Token, default);

        Assert.IsType<NotFoundResult>(result.Result);
    }
}
