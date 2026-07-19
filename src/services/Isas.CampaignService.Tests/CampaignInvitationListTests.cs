using Isas.CampaignService.DTOs;
using Isas.CampaignService.Models;
using Isas.CampaignService.Services;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging;
using Moq;

// 'CampaignService' vừa là namespace vừa là tên class → alias cho rõ ràng.
using CampaignSvc = Isas.CampaignService.Services.CampaignService;

namespace Isas.CampaignService.Tests;

/// <summary>
/// GET /campaign/{id}/invitations — danh sách lời mời đã phát (HR theo dõi phân phối).
/// Trạng thái suy read-time; "đã join" ghép từ membership (D2) chứ KHÔNG từ used_at (cột chưa từng ghi).
/// </summary>
public class CampaignInvitationListTests
{
    private static CampaignSvc NewService(CampaignDbContext db) =>
        new(db, Mock.Of<IFileService>(), Mock.Of<ILogger<CampaignSvc>>(), Mock.Of<IParserService>(),
            Mock.Of<ICriteriaSuggester>(), Mock.Of<IInvitationEmailPublisher>());

    private static CampaignInvitation SeedInvitation(
        CampaignDbContext db, Guid campaignId, string email,
        DateTime? emailSentAt = null, DateTime? revokedAt = null,
        DateTime? expiresAt = null, Guid? campaignCandidateId = null, DateTime? createdAt = null)
    {
        var id = Guid.NewGuid();
        var inv = new CampaignInvitation
        {
            Id = id,
            CampaignId = campaignId,
            CampaignCandidateId = campaignCandidateId,
            TokenHash = InvitationTokens.Hash(id.ToString("N")),
            Email = email,
            ExpiresAt = expiresAt ?? DateTime.UtcNow.AddDays(7),
            SentAt = DateTime.UtcNow,
            EmailSentAt = emailSentAt,
            RevokedAt = revokedAt,
            CreatedAt = createdAt ?? DateTime.UtcNow
        };
        db.CampaignInvitations.Add(inv);
        return inv;
    }

    // DB9 — FK cv_submission thật (SQLite CÓ enforce FK): membership/invitation đường-2 không thể
    // trỏ vào id ma.
    private static CvSubmission SeedCvSubmission(CampaignDbContext db, Guid campaignId, string email)
    {
        var cv = new CvSubmission
        {
            Id = Guid.NewGuid(),
            CampaignId = campaignId,
            Email = email,
            ParseStatus = CvParseStatus.Done,
            Status = CvSubmissionStatus.Invited,
            CreatedAt = DateTime.UtcNow,
            UpdatedAt = DateTime.UtcNow
        };
        db.CvSubmissions.Add(cv);
        return cv;
    }

    private static CampaignMembership SeedMembership(
        CampaignDbContext db, Guid campaignId, string? email, Guid? cvSubmissionId, DateTime joinedAt)
    {
        var m = new CampaignMembership
        {
            Id = Guid.NewGuid(),
            CampaignId = campaignId,
            CvSubmissionId = cvSubmissionId,
            CandidateId = Guid.NewGuid(),
            Email = email,
            Status = MembershipStatus.Joined,
            JoinedAt = joinedAt,
            CreatedAt = joinedAt,
            UpdatedAt = joinedAt
        };
        db.CampaignMemberships.Add(m);
        return m;
    }

    // (a) Đường-1 (mời thẳng email) PHẢI xuất hiện — đây là lỗ chính: nó không sinh cv_submission
    // nên GET /candidates không bao giờ thấy, và created[] của POST thì mất sau khi refresh.
    [Fact]
    public async Task Duong1_KhongCoCvSubmission_VanListRaDuoc()
    {
        using var tdb = new CampaignTestDb();
        var owner = Guid.NewGuid();
        var camp = CampaignTestDb.NewCampaign(owner, CampaignStatus.Active);
        tdb.Db.Campaigns.Add(camp);
        SeedInvitation(tdb.Db, camp.Id, "a@example.com", emailSentAt: DateTime.UtcNow);
        await tdb.Db.SaveChangesAsync();

        var rows = await NewService(tdb.NewContext()).GetInvitationsAsync(owner, camp.Id, null, default);

        var row = Assert.Single(rows);
        Assert.Equal("a@example.com", row.Email);
        Assert.Equal(InvitationDeliveryStatus.Sent, row.Status);
        Assert.NotNull(row.EmailSentAt);
        Assert.Null(row.CampaignCandidateId);   // đường-1
        Assert.Null(row.JoinedAt);
    }

    // (b) Queued vs Sent — phân biệt "đã vào outbox" (SentAt) với "SMTP đã gửi thật" (EmailSentAt, DB2b).
    // Đây chính là câu hỏi HR cần trả lời: mail tới nơi chưa.
    [Fact]
    public async Task ChuaGuiSmtp_La_Queued_GuiRoi_La_Sent()
    {
        using var tdb = new CampaignTestDb();
        var owner = Guid.NewGuid();
        var camp = CampaignTestDb.NewCampaign(owner, CampaignStatus.Active);
        tdb.Db.Campaigns.Add(camp);
        SeedInvitation(tdb.Db, camp.Id, "chua@example.com");                                  // EmailSentAt null
        SeedInvitation(tdb.Db, camp.Id, "roi@example.com", emailSentAt: DateTime.UtcNow);
        await tdb.Db.SaveChangesAsync();

        var rows = await NewService(tdb.NewContext()).GetInvitationsAsync(owner, camp.Id, null, default);

        Assert.Equal(InvitationDeliveryStatus.Queued,
            rows.Single(r => r.Email == "chua@example.com").Status);
        Assert.Equal(InvitationDeliveryStatus.Sent,
            rows.Single(r => r.Email == "roi@example.com").Status);
    }

    // (c) Có membership → Joined + JoinedAt, ghép được cả 2 đường (email cho đường-1, cv_submission_id
    // cho đường-2). Email so case-insensitive: đường-1 chỉ Trim(), đường-2 lowercase từ C13.
    [Fact]
    public async Task CoMembership_ThanhJoined_GhepCa2Duong_EmailCaseInsensitive()
    {
        using var tdb = new CampaignTestDb();
        var owner = Guid.NewGuid();
        var camp = CampaignTestDb.NewCampaign(owner, CampaignStatus.Active);
        tdb.Db.Campaigns.Add(camp);

        var joinedAt = DateTime.UtcNow.AddHours(-2);
        var cvId = SeedCvSubmission(tdb.Db, camp.Id, "duong2@example.com").Id;
        SeedInvitation(tdb.Db, camp.Id, "Duong1@Example.com", emailSentAt: DateTime.UtcNow);
        SeedInvitation(tdb.Db, camp.Id, "duong2@example.com", emailSentAt: DateTime.UtcNow,
            campaignCandidateId: cvId);
        SeedMembership(tdb.Db, camp.Id, "duong1@example.com", null, joinedAt);          // ghép theo email
        SeedMembership(tdb.Db, camp.Id, null, cvId, joinedAt);                          // ghép theo cv_submission
        await tdb.Db.SaveChangesAsync();

        var rows = await NewService(tdb.NewContext()).GetInvitationsAsync(owner, camp.Id, null, default);

        Assert.All(rows, r => Assert.Equal(InvitationDeliveryStatus.Joined, r.Status));
        Assert.All(rows, r => Assert.NotNull(r.JoinedAt));
    }

    // (d) Hết hạn mà chưa join → Expired (không phải cứ EmailSentAt là mãi mãi "Sent").
    [Fact]
    public async Task QuaHan_ChuaJoin_ThanhExpired()
    {
        using var tdb = new CampaignTestDb();
        var owner = Guid.NewGuid();
        var camp = CampaignTestDb.NewCampaign(owner, CampaignStatus.Active);
        tdb.Db.Campaigns.Add(camp);
        SeedInvitation(tdb.Db, camp.Id, "het@example.com",
            emailSentAt: DateTime.UtcNow.AddDays(-9), expiresAt: DateTime.UtcNow.AddDays(-1));
        await tdb.Db.SaveChangesAsync();

        var rows = await NewService(tdb.NewContext()).GetInvitationsAsync(owner, camp.Id, null, default);

        Assert.Equal(InvitationDeliveryStatus.Expired, Assert.Single(rows).Status);
    }

    // (e) Sau reissue (D4): lời mời CŨ phải là Revoked, KHÔNG được "thơm lây" Joined của lời mời mới
    // cùng email — đây là lý do Revoked xếp trước Joined trong thứ tự suy.
    [Fact]
    public async Task SauReissue_LoiMoiCu_LaRevoked_DuCungEmailDaJoin()
    {
        using var tdb = new CampaignTestDb();
        var owner = Guid.NewGuid();
        var camp = CampaignTestDb.NewCampaign(owner, CampaignStatus.Active);
        tdb.Db.Campaigns.Add(camp);

        var email = "reissued@example.com";
        var old = SeedInvitation(tdb.Db, camp.Id, email,
            emailSentAt: DateTime.UtcNow.AddDays(-1), revokedAt: DateTime.UtcNow.AddHours(-3),
            createdAt: DateTime.UtcNow.AddDays(-1));
        var fresh = SeedInvitation(tdb.Db, camp.Id, email, emailSentAt: DateTime.UtcNow);
        SeedMembership(tdb.Db, camp.Id, email, null, DateTime.UtcNow.AddMinutes(-5));
        await tdb.Db.SaveChangesAsync();

        var rows = await NewService(tdb.NewContext()).GetInvitationsAsync(owner, camp.Id, null, default);

        Assert.Equal(InvitationDeliveryStatus.Revoked, rows.Single(r => r.Id == old.Id).Status);
        Assert.Equal(InvitationDeliveryStatus.Joined, rows.Single(r => r.Id == fresh.Id).Status);
    }

    // (f) Lọc ?status= — chính là câu hỏi gốc "ứng viên nào đã được gửi mail".
    [Fact]
    public async Task LocTheoStatus_ChiTraDungNhom()
    {
        using var tdb = new CampaignTestDb();
        var owner = Guid.NewGuid();
        var camp = CampaignTestDb.NewCampaign(owner, CampaignStatus.Active);
        tdb.Db.Campaigns.Add(camp);
        SeedInvitation(tdb.Db, camp.Id, "sent@example.com", emailSentAt: DateTime.UtcNow);
        SeedInvitation(tdb.Db, camp.Id, "queued@example.com");
        await tdb.Db.SaveChangesAsync();

        var svc = NewService(tdb.NewContext());

        var sent = await svc.GetInvitationsAsync(owner, camp.Id, "sent", default);   // case-insensitive
        Assert.Equal("sent@example.com", Assert.Single(sent).Email);

        var queued = await svc.GetInvitationsAsync(owner, camp.Id, "Queued", default);
        Assert.Equal("queued@example.com", Assert.Single(queued).Email);
    }

    // (g) Ngoài org → 404 (leak-avoidance, nhất quán GetCandidates/results).
    [Fact]
    public async Task NgoaiOrg_Nem404()
    {
        using var tdb = new CampaignTestDb();
        var owner = Guid.NewGuid();
        var camp = CampaignTestDb.NewCampaign(owner, CampaignStatus.Active);
        tdb.Db.Campaigns.Add(camp);
        SeedInvitation(tdb.Db, camp.Id, "a@example.com");
        await tdb.Db.SaveChangesAsync();

        var svc = NewService(tdb.NewContext());

        await Assert.ThrowsAsync<KeyNotFoundException>(
            () => svc.GetInvitationsAsync(Guid.NewGuid(), camp.Id, null, default));
    }

    // (h) Lời mời của campaign KHÁC không lẫn sang; membership campaign khác không làm sai trạng thái.
    [Fact]
    public async Task KhongLanDuLieuCampaignKhac()
    {
        using var tdb = new CampaignTestDb();
        var owner = Guid.NewGuid();
        var mine = CampaignTestDb.NewCampaign(owner, CampaignStatus.Active);
        var other = CampaignTestDb.NewCampaign(owner, CampaignStatus.Active);
        tdb.Db.Campaigns.AddRange(mine, other);

        SeedInvitation(tdb.Db, mine.Id, "shared@example.com", emailSentAt: DateTime.UtcNow);
        SeedInvitation(tdb.Db, other.Id, "other@example.com", emailSentAt: DateTime.UtcNow);
        // Cùng email nhưng join ở campaign KHÁC → không được tính là đã join ở campaign này.
        SeedMembership(tdb.Db, other.Id, "shared@example.com", null, DateTime.UtcNow);
        await tdb.Db.SaveChangesAsync();

        var rows = await NewService(tdb.NewContext()).GetInvitationsAsync(owner, mine.Id, null, default);

        var row = Assert.Single(rows);
        Assert.Equal("shared@example.com", row.Email);
        Assert.Equal(InvitationDeliveryStatus.Sent, row.Status);   // KHÔNG phải Joined
    }
}
