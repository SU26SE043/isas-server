using System.Text.Json;
using Isas.CampaignService.Models;
using Isas.CampaignService.Services;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging;
using Moq;

using CampaignSvc = Isas.CampaignService.Services.CampaignService;

namespace Isas.CampaignService.Tests;

/// <summary>
/// DB2b — Transactional Outbox enqueue: 3 site tạo invitation (CreateInvitations / InviteShortlisted /
/// ReissueInvitation) ghi outbox-row CÙNG SaveChanges với invitation (không dual-write). Kiểm Payload
/// (JSON InvitationEmailJob) round-trip đúng token/email/campaign/invitation + KHÔNG publish trực tiếp.
/// </summary>
public class OutboxEnqueueTests
{
    private static readonly JsonSerializerOptions JsonOptions = new() { PropertyNameCaseInsensitive = true };

    private static CampaignSvc NewService(CampaignDbContext db, IInvitationEmailPublisher publisher) =>
        new(db, Mock.Of<IFileService>(), Mock.Of<ILogger<CampaignSvc>>(), Mock.Of<IParserService>(),
            Mock.Of<ICriteriaSuggester>(), publisher);

    // (a) CreateInvitations — outbox-row Payload khớp invitation (token/email/campaign/invitation).
    [Fact]
    public async Task CreateInvitations_GhiOutboxRow_PayloadDung_CungTransaction()
    {
        using var tdb = new CampaignTestDb();
        var owner = Guid.NewGuid();
        var camp = CampaignTestDb.NewCampaign(owner, CampaignStatus.Active);
        camp.Title = "Backend Q3";
        tdb.Db.Campaigns.Add(camp);
        await tdb.Db.SaveChangesAsync();

        var publisher = new Mock<IInvitationEmailPublisher>();
        var svc = NewService(tdb.NewContext(), publisher.Object);

        await svc.CreateInvitationsAsync(owner, owner, camp.Id, new List<string> { "a@example.com" }, default);

        using var check = tdb.NewContext();
        var inv = await check.CampaignInvitations.SingleAsync(i => i.CampaignId == camp.Id);
        var row = await check.OutboxMessages.SingleAsync();

        Assert.Equal(OutboxMessage.InvitationEmailType, row.Type);
        Assert.Equal(inv.Id, row.InvitationId);
        Assert.Equal(camp.Id, row.CampaignId);
        Assert.Null(row.PublishedAt);
        Assert.Equal(0, row.Attempts);

        var job = JsonSerializer.Deserialize<InvitationEmailJob>(row.Payload, JsonOptions)!;
        Assert.Equal(inv.Id, job.InvitationId);
        Assert.Equal(camp.Id, job.CampaignId);
        Assert.Equal("a@example.com", job.Email);
        Assert.Equal(inv.Token, job.Token);
        Assert.Equal("Backend Q3", job.CampaignTitle);

        // KHÔNG publish trực tiếp (dispatcher là đường phát duy nhất).
        publisher.Verify(p => p.PublishAsync(It.IsAny<InvitationEmailJob>(), It.IsAny<CancellationToken>()), Times.Never);
    }

    // (b) ReissueInvitation — revoke cũ + tạo mới + outbox-row đều trong 1 SaveChanges.
    [Fact]
    public async Task Reissue_GhiOutboxRow_ChoLoiMoiMoi()
    {
        using var tdb = new CampaignTestDb();
        var owner = Guid.NewGuid();
        var camp = CampaignTestDb.NewCampaign(owner, CampaignStatus.Active);
        tdb.Db.Campaigns.Add(camp);
        var old = new CampaignInvitation
        {
            Id = Guid.NewGuid(),
            CampaignId = camp.Id,
            Token = Guid.NewGuid().ToString("N"),
            Email = "reissue@example.com",
            CreatedAt = DateTime.UtcNow
        };
        tdb.Db.CampaignInvitations.Add(old);
        await tdb.Db.SaveChangesAsync();

        var publisher = new Mock<IInvitationEmailPublisher>();
        var svc = NewService(tdb.NewContext(), publisher.Object);

        var fresh = await svc.ReissueInvitationAsync(owner, owner, camp.Id, old.Id, default);

        using var check = tdb.NewContext();
        var row = await check.OutboxMessages.SingleAsync();
        Assert.Equal(fresh.Id, row.InvitationId);   // outbox cho lời mời MỚI, không phải cũ

        var job = JsonSerializer.Deserialize<InvitationEmailJob>(row.Payload, JsonOptions)!;
        Assert.Equal(fresh.Id, job.InvitationId);
        Assert.Equal("reissue@example.com", job.Email);

        publisher.Verify(p => p.PublishAsync(It.IsAny<InvitationEmailJob>(), It.IsAny<CancellationToken>()), Times.Never);
    }
}
