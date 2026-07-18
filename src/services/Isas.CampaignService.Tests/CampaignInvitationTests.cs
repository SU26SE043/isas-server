using Isas.CampaignService.Models;
using Isas.CampaignService.Services;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging;
using Moq;

// 'CampaignService' vừa là namespace vừa là tên class → alias cho rõ ràng.
using CampaignSvc = Isas.CampaignService.Services.CampaignService;

namespace Isas.CampaignService.Tests;

/// <summary>
/// D1 — Distribution đường 1: POST /campaign/{id}/invitations (mời thẳng qua danh sách email).
/// </summary>
public class CampaignInvitationTests
{
    private static CampaignSvc NewService(CampaignDbContext db, IInvitationEmailPublisher? emailPublisher = null) =>
        new(db, Mock.Of<IFileService>(), Mock.Of<ILogger<CampaignSvc>>(), Mock.Of<IParserService>(),
            Mock.Of<ICriteriaSuggester>(), emailPublisher ?? Mock.Of<IInvitationEmailPublisher>());

    // (a) email hợp lệ → tạo row có token + đẩy job email queue cho mỗi invitation
    [Fact]
    public async Task ValidEmails_TaoRows_CoToken_VaDayJobEmail()
    {
        using var tdb = new CampaignTestDb();
        var owner = Guid.NewGuid();
        var camp = CampaignTestDb.NewCampaign(owner, CampaignStatus.Active);
        tdb.Db.Campaigns.Add(camp);
        await tdb.Db.SaveChangesAsync();

        var publisher = new Mock<IInvitationEmailPublisher>();
        var svc = NewService(tdb.NewContext(), publisher.Object);

        var result = await svc.CreateInvitationsAsync(owner, owner, camp.Id,
            new List<string> { "a@example.com", "b@example.com" }, default);

        Assert.Equal(2, result.Created.Count);
        Assert.Empty(result.Failed);

        using var check = tdb.NewContext();
        var rows = await check.CampaignInvitations.Where(i => i.CampaignId == camp.Id).ToListAsync();
        Assert.Equal(2, rows.Count);
        Assert.All(rows, r => Assert.False(string.IsNullOrWhiteSpace(r.TokenHash)));
        Assert.All(rows, r => Assert.NotNull(r.SentAt));
        // token phải duy nhất
        Assert.Equal(rows.Count, rows.Select(r => r.TokenHash).Distinct().Count());

        // DB2b — outbox: mỗi invitation hợp lệ → 1 outbox-row (ghi CÙNG transaction, KHÔNG publish trực tiếp)
        var outbox = await check.OutboxMessages.ToListAsync();
        Assert.Equal(2, outbox.Count);
        Assert.All(outbox, m => Assert.Null(m.PublishedAt));   // chưa gửi (dispatcher publish sau)
        Assert.Equal(
            rows.Select(r => r.Id).OrderBy(x => x),
            outbox.Select(m => m.InvitationId).OrderBy(x => x));
        // service KHÔNG còn publish trực tiếp (dual-write) — dispatcher là đường phát duy nhất
        publisher.Verify(p => p.PublishAsync(It.IsAny<InvitationEmailJob>(), It.IsAny<CancellationToken>()), Times.Never);
    }

    // (b) trộn email hợp lệ/hỏng → hỏng vào failed[], hợp lệ vẫn được tạo (không chặn cả batch)
    [Fact]
    public async Task MixedValidInvalid_InvalidVaoFailed_ValidVanTao()
    {
        using var tdb = new CampaignTestDb();
        var owner = Guid.NewGuid();
        var camp = CampaignTestDb.NewCampaign(owner, CampaignStatus.Active);
        tdb.Db.Campaigns.Add(camp);
        await tdb.Db.SaveChangesAsync();

        var svc = NewService(tdb.NewContext());

        var result = await svc.CreateInvitationsAsync(owner, owner, camp.Id,
            new List<string> { "good@example.com", "not-an-email", "" }, default);

        Assert.Single(result.Created);
        Assert.Equal("good@example.com", result.Created[0].Email);
        Assert.Equal(2, result.Failed.Count);
        Assert.Contains(result.Failed, f => f.Email == "not-an-email");

        using var check = tdb.NewContext();
        var rows = await check.CampaignInvitations.Where(i => i.CampaignId == camp.Id).ToListAsync();
        Assert.Single(rows);
    }

    // (c) dedup: trùng trong cùng request + trùng với invitation đã có → chỉ 1 row, phần còn lại vào failed[]
    [Fact]
    public async Task Dedup_TrongRequest_VaVoiInvitationDaCo()
    {
        using var tdb = new CampaignTestDb();
        var owner = Guid.NewGuid();
        var camp = CampaignTestDb.NewCampaign(owner, CampaignStatus.Active);
        tdb.Db.Campaigns.Add(camp);
        tdb.Db.CampaignInvitations.Add(new CampaignInvitation
        {
            Id = Guid.NewGuid(),
            CampaignId = camp.Id,
            TokenHash = InvitationTokens.Hash("existing-token"),
            ExpiresAt = DateTime.UtcNow.AddDays(7),
            Email = "already@example.com",
            CreatedAt = DateTime.UtcNow
        });
        await tdb.Db.SaveChangesAsync();

        var svc = NewService(tdb.NewContext());

        var result = await svc.CreateInvitationsAsync(owner, owner, camp.Id,
            new List<string> { "dup@example.com", "DUP@example.com", "already@example.com" }, default);

        // chỉ "dup@example.com" (lần đầu) được tạo; "DUP@..." (trùng trong request) và
        // "already@example.com" (đã tồn tại) đều rơi vào failed[]
        Assert.Single(result.Created);
        Assert.Equal("dup@example.com", result.Created[0].Email);
        Assert.Equal(2, result.Failed.Count);

        using var check = tdb.NewContext();
        var rows = await check.CampaignInvitations.Where(i => i.CampaignId == camp.Id).ToListAsync();
        Assert.Equal(2, rows.Count);   // 1 đã có sẵn + 1 mới tạo
    }

    // (d) vượt cap max_candidates → 4xx (ArgumentException, controller map BadRequest), không tạo dở dang
    [Fact]
    public async Task VuotCap_MaxCandidates_NemArgumentException()
    {
        using var tdb = new CampaignTestDb();
        var owner = Guid.NewGuid();
        var camp = CampaignTestDb.NewCampaign(owner, CampaignStatus.Active);
        camp.MaxCandidates = 2;
        tdb.Db.Campaigns.Add(camp);
        tdb.Db.CampaignInvitations.Add(new CampaignInvitation
        {
            Id = Guid.NewGuid(),
            CampaignId = camp.Id,
            TokenHash = InvitationTokens.Hash("existing-token"),
            ExpiresAt = DateTime.UtcNow.AddDays(7),
            Email = "already@example.com",
            CreatedAt = DateTime.UtcNow
        });
        await tdb.Db.SaveChangesAsync();

        var svc = NewService(tdb.NewContext());

        // đã có 1 invitation, cap = 2 → mời thêm 2 người nữa (tổng 3) vượt cap
        await Assert.ThrowsAsync<ArgumentException>(() =>
            svc.CreateInvitationsAsync(owner, owner, camp.Id,
                new List<string> { "new1@example.com", "new2@example.com" }, default));

        // không tạo dở dang: vẫn chỉ có 1 invitation cũ
        using var check = tdb.NewContext();
        var rows = await check.CampaignInvitations.Where(i => i.CampaignId == camp.Id).ToListAsync();
        Assert.Single(rows);
    }

    // Guard: campaign không Active → InvalidOperationException (controller map 409)
    [Fact]
    public async Task CampaignKhongActive_NemInvalidOperationException()
    {
        using var tdb = new CampaignTestDb();
        var owner = Guid.NewGuid();
        var camp = CampaignTestDb.NewCampaign(owner, CampaignStatus.Draft);
        tdb.Db.Campaigns.Add(camp);
        await tdb.Db.SaveChangesAsync();

        var svc = NewService(tdb.NewContext());

        await Assert.ThrowsAsync<InvalidOperationException>(() =>
            svc.CreateInvitationsAsync(owner, owner, camp.Id, new List<string> { "a@example.com" }, default));
    }
}
