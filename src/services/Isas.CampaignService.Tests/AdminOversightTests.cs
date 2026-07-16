using Isas.CampaignService.Models;
using Isas.CampaignService.Services;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging;
using Moq;

using CampaignSvc = Isas.CampaignService.Services.CampaignService;

namespace Isas.CampaignService.Tests;

/// <summary>
/// AUTH-7 — PlatformAdmin oversight (read-only, cross-org). ListAllCampaigns trả campaign của MỌI org
/// (không lọc org của caller), tôn trọng soft-delete (D11), lọc optional status/orgId.
/// Idiom helpers theo CampaignResultsTests (CampaignTestDb.NewCampaign, NewService).
/// </summary>
public class AdminOversightTests
{
    private static CampaignSvc NewService(CampaignDbContext db) =>
        new(db, Mock.Of<IFileService>(), Mock.Of<ILogger<CampaignSvc>>(),
            Mock.Of<IParserService>(), Mock.Of<ICriteriaSuggester>(),
            Mock.Of<IInvitationEmailPublisher>());

    private static Campaign Seed(CampaignDbContext db, Guid orgId, CampaignStatus status, string title, bool deleted = false)
    {
        var c = CampaignTestDb.NewCampaign(orgId, status);
        c.Title = title;
        if (deleted) c.DeletedAt = DateTime.UtcNow;
        db.Campaigns.Add(c);
        db.SaveChanges();
        return c;
    }

    [Fact]
    public async Task ListAll_ReturnsCampaignsAcrossOrgs()
    {
        using var tdb = new CampaignTestDb();
        var orgA = Guid.NewGuid();
        var orgB = Guid.NewGuid();
        Seed(tdb.Db, orgA, CampaignStatus.Active, "A1");
        Seed(tdb.Db, orgA, CampaignStatus.Draft, "A2");
        Seed(tdb.Db, orgB, CampaignStatus.Active, "B1");

        var res = await NewService(tdb.NewContext()).ListAllCampaignsAsync(null, null, default);

        Assert.Equal(3, res.Count);
        Assert.Contains(res, c => c.Title == "A1" && c.OrgId == orgA);
        Assert.Contains(res, c => c.Title == "B1" && c.OrgId == orgB);
    }

    [Fact]
    public async Task ListAll_ExcludesSoftDeleted()
    {
        using var tdb = new CampaignTestDb();
        var org = Guid.NewGuid();
        Seed(tdb.Db, org, CampaignStatus.Active, "Alive");
        Seed(tdb.Db, org, CampaignStatus.Active, "Gone", deleted: true);

        var res = await NewService(tdb.NewContext()).ListAllCampaignsAsync(null, null, default);

        Assert.Single(res);
        Assert.Equal("Alive", res[0].Title);
    }

    [Fact]
    public async Task ListAll_FilterByStatusAndOrg()
    {
        using var tdb = new CampaignTestDb();
        var orgA = Guid.NewGuid();
        var orgB = Guid.NewGuid();
        Seed(tdb.Db, orgA, CampaignStatus.Active, "A-active");
        Seed(tdb.Db, orgA, CampaignStatus.Draft, "A-draft");
        Seed(tdb.Db, orgB, CampaignStatus.Active, "B-active");

        var byStatus = await NewService(tdb.NewContext()).ListAllCampaignsAsync("Active", null, default);
        Assert.Equal(2, byStatus.Count);
        Assert.All(byStatus, c => Assert.Equal("Active", c.Status));

        var byOrg = await NewService(tdb.NewContext()).ListAllCampaignsAsync(null, orgA, default);
        Assert.Equal(2, byOrg.Count);
        Assert.All(byOrg, c => Assert.Equal(orgA, c.OrgId));

        var both = await NewService(tdb.NewContext()).ListAllCampaignsAsync("Active", orgA, default);
        Assert.Single(both);
        Assert.Equal("A-active", both[0].Title);
    }
}
