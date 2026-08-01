using Isas.CampaignService.DTOs;
using Isas.CampaignService.Models;
using Isas.CampaignService.Services;
using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.Logging;
using Moq;

using CampaignSvc = Isas.CampaignService.Services.CampaignService;

namespace Isas.CampaignService.Tests;

public sealed class CampaignEntitlementTests
{
    private sealed class Entitlements(CampaignEntitlement value) : IEntitlementClient
    {
        public Task<CampaignEntitlement> ResolveOrgAsync(Guid orgId, CancellationToken ct = default) => Task.FromResult(value);
    }

    private static readonly CampaignEntitlement Business = new("resolved", "business", 1, 10, 200, true, true, true);
    private static CampaignSvc Service(CampaignDbContext db, CampaignEntitlement entitlement) => new(
        db, Mock.Of<IFileService>(), Mock.Of<ILogger<CampaignSvc>>(), Mock.Of<IParserService>(),
        Mock.Of<ICriteriaSuggester>(), Mock.Of<IInvitationEmailPublisher>(), entitlements: new Entitlements(entitlement));

    private static CreateCampaignRequest Request(int? cap = 25) => new()
    {
        Title = "T8", Domain = "BE", MaxCandidates = cap, TimeLimitMinutes = 30,
        StartsAt = DateTime.UtcNow.AddMinutes(1), ExpiresAt = DateTime.UtcNow.AddDays(1),
        Questions = [new QuestionItem { QuestionText = "Q" }]
    };

    [Fact]
    public async Task Starter_SecondActiveCampaign_IsForbidden()
    {
        using var tdb = new CampaignTestDb(); var db = tdb.NewContext(); var org = Guid.NewGuid();
        db.Campaigns.Add(new Campaign { Id = Guid.NewGuid(), OrgId = org, Title = "active", Status = CampaignStatus.Active, StartsAt = DateTime.UtcNow, CreatedAt = DateTime.UtcNow, UpdatedAt = DateTime.UtcNow });
        await db.SaveChangesAsync();
        await Assert.ThrowsAsync<EntitlementForbiddenException>(() => Service(db, CampaignEntitlement.Starter).CreateCampaignAsync(org, org, Request(), default));
    }

    [Fact]
    public async Task Business_CreateWithinCap_Succeeds_AndOverCapFails()
    {
        using var tdb = new CampaignTestDb(); var org = Guid.NewGuid();
        var ok = await Service(tdb.NewContext(), Business).CreateCampaignAsync(org, org, Request(200), default);
        Assert.Equal(200, ok.MaxCandidates);
        await Assert.ThrowsAsync<ArgumentException>(() => Service(tdb.NewContext(), Business).CreateCampaignAsync(org, org, Request(201), default));
    }

    [Fact]
    public async Task Starter_BlocksAdaptiveAndGrounding_CreateAndUpdate()
    {
        using var tdb = new CampaignTestDb(); var db = tdb.NewContext(); var org = Guid.NewGuid();
        var adaptive = Request(); adaptive.AdaptiveEnabled = true;
        await Assert.ThrowsAsync<EntitlementForbiddenException>(() => Service(db, CampaignEntitlement.Starter).CreateCampaignAsync(org, org, adaptive, default));

        var campaign = await Service(db, Business).CreateCampaignAsync(org, org, Request(), default);
        await Assert.ThrowsAsync<EntitlementForbiddenException>(() => Service(db, CampaignEntitlement.Starter).UpdateCampaignAsync(org, org, campaign.Id, new UpdateCampaignRequest { GroundingEnabled = true }, default));
    }

    [Fact]
    public async Task Starter_FallbackCapsInviteAndScreening()
    {
        using var tdb = new CampaignTestDb(); var db = tdb.NewContext(); var org = Guid.NewGuid();
        var campaignResponse = await Service(db, Business).CreateCampaignAsync(org, org, Request(200), default);
        var campaign = db.Campaigns.Single(c => c.Id == campaignResponse.Id); campaign.Status = CampaignStatus.Active;
        for (var i = 0; i < 25; i++) db.CampaignInvitations.Add(new CampaignInvitation { Id = Guid.NewGuid(), CampaignId = campaign.Id, TokenHash = Guid.NewGuid().ToString(), Email = $"{i}@x.test", ExpiresAt = DateTime.UtcNow.AddDays(1), CreatedAt = DateTime.UtcNow });
        await db.SaveChangesAsync();
        await Assert.ThrowsAsync<ArgumentException>(() => Service(db, CampaignEntitlement.Starter).CreateInvitationsAsync(org, org, campaign.Id, ["next@x.test"], default));

        db.CampaignInvitations.RemoveRange(db.CampaignInvitations);
        for (var i = 0; i < 25; i++) db.CvSubmissions.Add(new CvSubmission { Id = Guid.NewGuid(), CampaignId = campaign.Id, Status = CvSubmissionStatus.Filtered, ParseStatus = CvParseStatus.Done, CreatedAt = DateTime.UtcNow, UpdatedAt = DateTime.UtcNow });
        await db.SaveChangesAsync();
        var files = new FormFileCollection { new FormFile(new MemoryStream([1]), 0, 1, "files", "cv.pdf") { Headers = new HeaderDictionary(), ContentType = "application/pdf" } };
        await Assert.ThrowsAsync<ArgumentException>(() => Service(db, CampaignEntitlement.Starter).ScreenCandidatesAsync(org, org, campaign.Id, files, default));
    }
}
