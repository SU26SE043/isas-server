using System.Security.Claims;
using Isas.AuthService.Controllers;
using Isas.AuthService.DTOs;
using Isas.AuthService.Services;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc;
using Moq;

namespace Isas.AuthService.Tests;

/// <summary>
/// A6 (AUTH-4/AUTH-8) — chỉ OrgAdmin quản thành viên org. Test tầng controller (biên đọc claim OFFLINE,
/// GEN-3): HrMember/Candidate/không claim → 403 (Forbid); OrgAdmin qua guard tới logic. Idiom theo
/// Payment BillingAuthorizationTests (ClaimsPrincipal giả gắn vào HttpContext).
/// </summary>
public class OrgMembersControllerTests
{
    private static ClaimsPrincipal Principal(Guid userId, Guid? orgId, string? orgRole)
    {
        var claims = new List<Claim> { new(ClaimTypes.NameIdentifier, userId.ToString()) };
        if (orgId is Guid g) claims.Add(new Claim("org_id", g.ToString()));
        if (orgRole is not null) claims.Add(new Claim("org_role", orgRole));
        return new ClaimsPrincipal(new ClaimsIdentity(claims, "Test"));
    }

    private static ClaimsPrincipal OrgAdmin(Guid orgId) => Principal(Guid.NewGuid(), orgId, "OrgAdmin");
    private static ClaimsPrincipal HrMember() => Principal(Guid.NewGuid(), Guid.NewGuid(), "HrMember");
    private static ClaimsPrincipal Candidate() => Principal(Guid.NewGuid(), orgId: null, orgRole: null);

    private static OrgMembersController Controller(Mock<IAuthService> svc, ClaimsPrincipal user)
    {
        var ctrl = new OrgMembersController(svc.Object)
        {
            ControllerContext = new ControllerContext { HttpContext = new DefaultHttpContext { User = user } }
        };
        return ctrl;
    }

    private static Mock<IAuthService> MockSvc()
    {
        var svc = new Mock<IAuthService>();
        svc.Setup(s => s.AddOrgMemberAsync(It.IsAny<Guid>(), It.IsAny<string>(), It.IsAny<string?>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(new OrgMemberResponse { UserId = Guid.NewGuid(), Email = "hr@acme.test", OrgRole = "HrMember" });
        svc.Setup(s => s.ListOrgMembersAsync(It.IsAny<Guid>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(new List<OrgMemberResponse>());
        return svc;
    }

    private static readonly AddOrgMemberRequest ValidReq = new() { Email = "hr@acme.test", FullName = "HR" };

    // ---------- POST /auth/org/members ----------

    [Fact]
    public async Task AddMember_HrMember_Returns403_ServiceNotCalled()
    {
        var svc = MockSvc();
        var ctrl = Controller(svc, HrMember());

        var result = await ctrl.AddMember(ValidReq);

        Assert.IsType<ForbidResult>(result.Result);
        svc.Verify(s => s.AddOrgMemberAsync(It.IsAny<Guid>(), It.IsAny<string>(), It.IsAny<string?>(), It.IsAny<CancellationToken>()), Times.Never);
    }

    [Fact]
    public async Task AddMember_Candidate_NoOrgClaim_Returns403()
    {
        var svc = MockSvc();
        var ctrl = Controller(svc, Candidate());

        var result = await ctrl.AddMember(ValidReq);

        Assert.IsType<ForbidResult>(result.Result);
        svc.Verify(s => s.AddOrgMemberAsync(It.IsAny<Guid>(), It.IsAny<string>(), It.IsAny<string?>(), It.IsAny<CancellationToken>()), Times.Never);
    }

    [Fact]
    public async Task AddMember_OrgAdmin_CallsServiceWithCallerOrg_Returns201()
    {
        var orgId = Guid.NewGuid();
        var svc = MockSvc();
        var ctrl = Controller(svc, OrgAdmin(orgId));

        var result = await ctrl.AddMember(ValidReq);

        Assert.IsType<CreatedAtActionResult>(result.Result);
        svc.Verify(s => s.AddOrgMemberAsync(orgId, "hr@acme.test", "HR", It.IsAny<CancellationToken>()), Times.Once);
    }

    [Fact]
    public async Task AddMember_OrgAdmin_DuplicateEmail_Returns409()
    {
        var svc = MockSvc();
        svc.Setup(s => s.AddOrgMemberAsync(It.IsAny<Guid>(), It.IsAny<string>(), It.IsAny<string?>(), It.IsAny<CancellationToken>()))
            .ThrowsAsync(new OrgMemberConflictException("Email is already a member of this organization"));
        var ctrl = Controller(svc, OrgAdmin(Guid.NewGuid()));

        var result = await ctrl.AddMember(ValidReq);

        Assert.IsType<ConflictObjectResult>(result.Result);
    }

    // ---------- GET /auth/org/members ----------

    [Fact]
    public async Task ListMembers_HrMember_Returns403()
    {
        var svc = MockSvc();
        var ctrl = Controller(svc, HrMember());

        var result = await ctrl.ListMembers();

        Assert.IsType<ForbidResult>(result.Result);
        svc.Verify(s => s.ListOrgMembersAsync(It.IsAny<Guid>(), It.IsAny<CancellationToken>()), Times.Never);
    }

    [Fact]
    public async Task ListMembers_OrgAdmin_ReturnsOk_ForCallerOrg()
    {
        var orgId = Guid.NewGuid();
        var svc = MockSvc();
        var ctrl = Controller(svc, OrgAdmin(orgId));

        var result = await ctrl.ListMembers();

        Assert.IsType<OkObjectResult>(result.Result);
        svc.Verify(s => s.ListOrgMembersAsync(orgId, It.IsAny<CancellationToken>()), Times.Once);
    }
}
