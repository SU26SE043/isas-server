using Isas.CampaignService.DTOs;
using Isas.CampaignService.Models;
using Isas.CampaignService.Services;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging;
using Moq;
using CampaignSvc = Isas.CampaignService.Services.CampaignService;

namespace Isas.CampaignService.Tests;

public class CampaignSlotServiceTests
{
    private static CampaignSvc Service(CampaignDbContext db) => new(db, Mock.Of<IFileService>(),
        Mock.Of<ILogger<CampaignSvc>>(), Mock.Of<IParserService>(), Mock.Of<ICriteriaSuggester>(), Mock.Of<IInvitationEmailPublisher>());
    private static CreateCampaignSlotRequest Slot(DateTime start, int capacity = 2) => new() { StartsAt = start, EndsAt = start.AddHours(1), Capacity = capacity };

    [Fact]
    public async Task CreateSlot_RequiresOwner_AndRejectsOverlap()
    {
        using var t = new CampaignTestDb(); var owner = Guid.NewGuid(); var campaign = CampaignTestDb.NewCampaign(owner);
        t.Db.Campaigns.Add(campaign); await t.Db.SaveChangesAsync(); var svc = Service(t.NewContext()); var start = DateTime.UtcNow.AddDays(1);
        await Assert.ThrowsAsync<KeyNotFoundException>(() => svc.CreateSlotAsync(Guid.NewGuid(), campaign.Id, Slot(start), default));
        await svc.CreateSlotAsync(owner, campaign.Id, Slot(start), default);
        await Assert.ThrowsAsync<InvalidOperationException>(() => svc.CreateSlotAsync(owner, campaign.Id, Slot(start.AddMinutes(30)), default));
    }

    [Fact]
    public async Task UpdateSlot_RejectsCapacityBelowAssignedInvitations()
    {
        using var t = new CampaignTestDb(); var owner = Guid.NewGuid(); var campaign = CampaignTestDb.NewCampaign(owner); var start = DateTime.UtcNow.AddDays(1);
        var slot = new CampaignSlot { Id = Guid.NewGuid(), CampaignId = campaign.Id, StartsAt = start, EndsAt = start.AddHours(1), Capacity = 2 };
        t.Db.AddRange(campaign, slot,
            new CampaignInvitation { Id = Guid.NewGuid(), CampaignId = campaign.Id, SlotId = slot.Id, TokenHash = "a", Email = "a@x.test", ExpiresAt = start.AddDays(2), CreatedAt = DateTime.UtcNow },
            new CampaignInvitation { Id = Guid.NewGuid(), CampaignId = campaign.Id, SlotId = slot.Id, TokenHash = "b", Email = "b@x.test", ExpiresAt = start.AddDays(2), CreatedAt = DateTime.UtcNow }); await t.Db.SaveChangesAsync();
        await Assert.ThrowsAsync<ArgumentException>(() => Service(t.NewContext()).UpdateSlotAsync(owner, campaign.Id, slot.Id, new UpdateCampaignSlotRequest { StartsAt = start, EndsAt = start.AddHours(1), Capacity = 1 }, default));
    }

    [Fact]
    public async Task DeleteSlot_RejectsWhenCandidateIsInProgress()
    {
        using var t = new CampaignTestDb(); var owner = Guid.NewGuid(); var campaign = CampaignTestDb.NewCampaign(owner); var start = DateTime.UtcNow.AddDays(1);
        var slot = new CampaignSlot { Id = Guid.NewGuid(), CampaignId = campaign.Id, StartsAt = start, EndsAt = start.AddHours(1), Capacity = 2 };
        t.Db.AddRange(campaign, slot, CampaignTestDb.NewMembership(campaign.Id, Guid.NewGuid())); await t.Db.SaveChangesAsync();
        var membership = await t.Db.CampaignMemberships.SingleAsync(); membership.SlotId = slot.Id; membership.InterviewStatus = InterviewProgressStatus.InProgress; await t.Db.SaveChangesAsync();
        await Assert.ThrowsAsync<InvalidOperationException>(() => Service(t.NewContext()).DeleteSlotAsync(owner, campaign.Id, slot.Id, default));
    }
}
