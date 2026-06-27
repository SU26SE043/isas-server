using Isas.AuthService.Models;
using Microsoft.EntityFrameworkCore;

namespace Isas.AuthService.Tests;

public class OrganizationTests
{
    // A1 (tasks.md): tạo 1 org + 1 member OK.
    [Fact]
    public void CanCreateOrganizationWithOneMember()
    {
        using var test = new AuthTestDb();

        var user = new User
        {
            Id = Guid.NewGuid(),
            UserName = "hr@acme.test",
            Email = "hr@acme.test"
        };
        var org = new Organization
        {
            Id = Guid.NewGuid(),
            Name = "Acme Corp",
            TaxCode = "0101234567",
            CreatedAt = DateTime.UtcNow
        };
        var member = new OrgMember
        {
            OrgId = org.Id,
            UserId = user.Id,
            OrgRole = OrgRole.OrgAdmin
        };

        test.Db.Users.Add(user);
        test.Db.Organizations.Add(org);
        test.Db.OrgMembers.Add(member);
        test.Db.SaveChanges();

        // đọc lại bằng context mới → chắc chắn đã persist, không phải cache
        using var verify = test.NewContext();
        var saved = verify.OrgMembers
            .Include(m => m.Organization)
            .Single();

        Assert.Equal(org.Id, saved.OrgId);
        Assert.Equal(user.Id, saved.UserId);
        Assert.Equal(OrgRole.OrgAdmin, saved.OrgRole);
        Assert.Equal("Acme Corp", saved.Organization.Name);
        Assert.Equal("0101234567", saved.Organization.TaxCode);
    }
}
