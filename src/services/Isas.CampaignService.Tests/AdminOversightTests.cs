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

    // Seed a campaign at an explicit CreatedAt — for deterministic keyset paging tests (DB8).
    private static Campaign SeedAt(CampaignDbContext db, Guid orgId, string title, DateTime createdAt)
    {
        var c = CampaignTestDb.NewCampaign(orgId, CampaignStatus.Active);
        c.Title = title;
        c.CreatedAt = createdAt;
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

        var res = await NewService(tdb.NewContext()).ListAllCampaignsAsync(null, null, null, null, default);

        Assert.Equal(3, res.Items.Count);
        Assert.Null(res.NextCursor);   // < default limit → last page (backward-compat: no cursor emitted)
        Assert.Contains(res.Items, c => c.Title == "A1" && c.OrgId == orgA);
        Assert.Contains(res.Items, c => c.Title == "B1" && c.OrgId == orgB);
    }

    [Fact]
    public async Task ListAll_ExcludesSoftDeleted()
    {
        using var tdb = new CampaignTestDb();
        var org = Guid.NewGuid();
        Seed(tdb.Db, org, CampaignStatus.Active, "Alive");
        Seed(tdb.Db, org, CampaignStatus.Active, "Gone", deleted: true);

        var res = await NewService(tdb.NewContext()).ListAllCampaignsAsync(null, null, null, null, default);

        Assert.Single(res.Items);
        Assert.Equal("Alive", res.Items[0].Title);
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

        var byStatus = await NewService(tdb.NewContext()).ListAllCampaignsAsync("Active", null, null, null, default);
        Assert.Equal(2, byStatus.Items.Count);
        Assert.All(byStatus.Items, c => Assert.Equal("Active", c.Status));

        var byOrg = await NewService(tdb.NewContext()).ListAllCampaignsAsync(null, orgA, null, null, default);
        Assert.Equal(2, byOrg.Items.Count);
        Assert.All(byOrg.Items, c => Assert.Equal(orgA, c.OrgId));

        var both = await NewService(tdb.NewContext()).ListAllCampaignsAsync("Active", orgA, null, null, default);
        Assert.Single(both.Items);
        Assert.Equal("A-active", both.Items[0].Title);
    }

    [Fact]
    public async Task ListAll_Keyset_PagesWithoutOverlapOrGap()
    {
        using var tdb = new CampaignTestDb();
        var org = Guid.NewGuid();
        var t0 = new DateTime(2026, 7, 1, 12, 0, 0, DateTimeKind.Utc);
        for (var i = 0; i < 5; i++)
            SeedAt(tdb.Db, org, $"C{i}", t0.AddMinutes(i));

        var seen = new List<string>();
        string? cursor = null;
        var pages = 0;
        do
        {
            var page = await NewService(tdb.NewContext()).ListAllCampaignsAsync(null, null, cursor, 2, default);
            Assert.True(page.Items.Count <= 2);
            seen.AddRange(page.Items.Select(c => c.Title));
            cursor = page.NextCursor;
            Assert.True(++pages <= 10, "paging did not terminate");
        } while (cursor is not null);

        Assert.Equal(new[] { "C4", "C3", "C2", "C1", "C0" }, seen.ToArray());
    }

    [Fact]
    public async Task ListAll_Keyset_TiebreakerOnIdenticalCreatedAt()
    {
        using var tdb = new CampaignTestDb();
        var org = Guid.NewGuid();
        var same = new DateTime(2026, 7, 2, 9, 0, 0, DateTimeKind.Utc);
        SeedAt(tdb.Db, org, "T1", same);
        SeedAt(tdb.Db, org, "T2", same);
        SeedAt(tdb.Db, org, "T3", same);

        var seen = new List<string>();
        string? cursor = null;
        for (var i = 0; i < 5 && (i == 0 || cursor is not null); i++)
        {
            var page = await NewService(tdb.NewContext()).ListAllCampaignsAsync(null, null, cursor, 1, default);
            seen.AddRange(page.Items.Select(c => c.Title));
            cursor = page.NextCursor;
        }

        Assert.Equal(3, seen.Count);
        Assert.Equal(3, seen.Distinct().Count());
    }

    [Fact]
    public async Task ListAll_MalformedCursor_ReturnsFirstPage()
    {
        using var tdb = new CampaignTestDb();
        var org = Guid.NewGuid();
        Seed(tdb.Db, org, CampaignStatus.Active, "X1");
        Seed(tdb.Db, org, CampaignStatus.Active, "X2");

        var page = await NewService(tdb.NewContext()).ListAllCampaignsAsync(null, null, "@@garbage@@", null, default);

        Assert.Equal(2, page.Items.Count);
    }
}
