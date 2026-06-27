using System.IdentityModel.Tokens.Jwt;
using Isas.AuthService.Models;
using Isas.AuthService.Services;
using Microsoft.Extensions.Configuration;

namespace Isas.AuthService.Tests;

public class JwtOrgClaimsTests
{
    private static JwtService NewJwtService()
    {
        var config = new ConfigurationBuilder()
            .AddInMemoryCollection(new Dictionary<string, string?>
            {
                ["Jwt:Key"] = "isas-test-signing-key-0123456789-abcdef", // ≥32 bytes cho HS256
                ["Jwt:Issuer"] = "isas-test",
                ["Jwt:Audience"] = "isas-test",
                ["Jwt:AccessTokenMinutes"] = "15"
            })
            .Build();
        return new JwtService(config);
    }

    private static User NewUser() => new()
    {
        Id = Guid.NewGuid(),
        UserName = "hr@acme.test",
        Email = "hr@acme.test"
    };

    // A2 (tasks.md): Employer thuộc org → token mang claim org_id + org_role.
    [Fact]
    public void AccessToken_OrgMember_CarriesOrgIdAndOrgRole()
    {
        var jwt = NewJwtService();
        var user = NewUser();
        var orgId = Guid.NewGuid();
        var membership = new OrgMember { OrgId = orgId, UserId = user.Id, OrgRole = OrgRole.OrgAdmin };

        var token = jwt.GenerateAccessToken(user, new[] { "Employer" }, membership);

        var decoded = new JwtSecurityTokenHandler().ReadJwtToken(token);
        Assert.Equal(orgId.ToString(), decoded.Claims.Single(c => c.Type == "org_id").Value);
        Assert.Equal("OrgAdmin", decoded.Claims.Single(c => c.Type == "org_role").Value);
    }

    // User không thuộc org (vd Candidate) → KHÔNG có org claim.
    [Fact]
    public void AccessToken_NonMember_HasNoOrgClaims()
    {
        var jwt = NewJwtService();
        var user = NewUser();

        var token = jwt.GenerateAccessToken(user, new[] { "Candidate" });

        var decoded = new JwtSecurityTokenHandler().ReadJwtToken(token);
        Assert.DoesNotContain(decoded.Claims, c => c.Type == "org_id");
        Assert.DoesNotContain(decoded.Claims, c => c.Type == "org_role");
    }
}
