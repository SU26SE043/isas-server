using Isas.CampaignService.Models;
using Isas.CampaignService.Services;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging;
using Moq;
using CampaignSvc = Isas.CampaignService.Services.CampaignService;

namespace Isas.CampaignService.Tests;

public class CampaignSlotAssignmentTests
{
    private static CampaignSvc Service(CampaignDbContext db) => new(db, Mock.Of<IFileService>(), Mock.Of<ILogger<CampaignSvc>>(), Mock.Of<IParserService>(), Mock.Of<ICriteriaSuggester>(), Mock.Of<IInvitationEmailPublisher>());
    private static CampaignSlot Slot(Guid campaignId, DateTime start, int capacity) => new() { Id=Guid.NewGuid(), CampaignId=campaignId, StartsAt=start, EndsAt=start.AddHours(1), Capacity=capacity };

    [Fact]
    public async Task Invitations_DistributeEvenly_AndRejectWhenAllSlotsFull()
    {
        using var t=new CampaignTestDb(); var owner=Guid.NewGuid(); var camp=CampaignTestDb.NewCampaign(owner, CampaignStatus.Active); var start=DateTime.UtcNow.AddDays(1);
        var a=Slot(camp.Id,start,2); var b=Slot(camp.Id,start.AddHours(2),2); t.Db.AddRange(camp,a,b); await t.Db.SaveChangesAsync();
        await Service(t.NewContext()).CreateInvitationsAsync(owner,owner,camp.Id,["a@x.test","b@x.test","c@x.test","d@x.test"],default);
        using(var read=t.NewContext()) { var assigned=await read.CampaignInvitations.GroupBy(i=>i.SlotId).Select(g=>g.Count()).ToListAsync(); Assert.Equal([2,2],assigned.Order()); }
        await Assert.ThrowsAsync<ArgumentException>(()=>Service(t.NewContext()).CreateInvitationsAsync(owner,owner,camp.Id,["e@x.test"],default));
    }

    [Fact]
    public async Task Reissue_KeepsExistingSlot()
    {
        using var t=new CampaignTestDb(); var owner=Guid.NewGuid(); var camp=CampaignTestDb.NewCampaign(owner, CampaignStatus.Active); var slot=Slot(camp.Id,DateTime.UtcNow.AddDays(1),2);
        var invitation=new CampaignInvitation { Id=Guid.NewGuid(),CampaignId=camp.Id,SlotId=slot.Id,TokenHash="x",Email="a@x.test",ExpiresAt=DateTime.UtcNow.AddDays(2),CreatedAt=DateTime.UtcNow };
        t.Db.AddRange(camp,slot,invitation); await t.Db.SaveChangesAsync(); var fresh=await Service(t.NewContext()).ReissueInvitationAsync(owner,owner,camp.Id,invitation.Id,default);
        using var read=t.NewContext(); Assert.Equal(slot.Id,(await read.CampaignInvitations.FindAsync(fresh.Id))!.SlotId);
    }
}
