using Isas.CampaignService.DTOs;
using Isas.CampaignService.Models;
using Isas.CampaignService.Services;
using Isas.Shared.Pagination;
using Microsoft.Extensions.Logging;
using Moq;

using CampaignSvc = Isas.CampaignService.Services.CampaignService;

namespace Isas.CampaignService.Tests;

/// <summary>
/// GET /campaign/{id}/invitations — keyset-paged + `?search=` (email) + `?status=` ĐẨY XUỐNG SQL.
/// Điểm mấu chốt: trạng thái tuy SUY read-time nhưng suy từ đúng các cột đang có nên diễn đạt lại
/// thành vị ngữ SQL được. Nếu lọc trong C# sau khi phân trang thì `?status=` chỉ đúng trong phạm vi
/// 1 trang ⇒ HR lọc "ai chưa nhận mail" sẽ thấy thiếu người mà không có gì báo lỗi.
/// Ràng buộc phải giữ: thứ tự ưu tiên Revoked → Joined → Expired → Sent → Queued (D4 reissue).
/// </summary>
public class InvitationListPagingTests
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

    private static void SeedMembership(
        CampaignDbContext db, Guid campaignId, string? email, Guid? cvSubmissionId, DateTime joinedAt)
        => db.CampaignMemberships.Add(new CampaignMembership
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
        });

    private static Campaign SeedCampaign(CampaignTestDb tdb, Guid orgId)
    {
        var camp = CampaignTestDb.NewCampaign(orgId, CampaignStatus.Active);
        tdb.Db.Campaigns.Add(camp);
        return camp;
    }

    // ── Phân trang ──────────────────────────────────────────────────────────────────────

    [Fact]
    public async Task PhanTrang_MoiNhatTruoc_KhongTrungKhongSot()
    {
        using var tdb = new CampaignTestDb();
        var org = Guid.NewGuid();
        var camp = SeedCampaign(tdb, org);
        var t0 = DateTime.UtcNow;
        for (var i = 0; i < 5; i++)
            SeedInvitation(tdb.Db, camp.Id, $"i{i}@x.com", createdAt: t0.AddHours(-i));   // i0 mới nhất
        await tdb.Db.SaveChangesAsync();

        var svc = NewService(tdb.NewContext());

        var p1 = await svc.GetInvitationsAsync(org, camp.Id, null, null, null, 2, default);
        Assert.Equal(new[] { "i0@x.com", "i1@x.com" }, p1.Items.Select(r => r.Email));
        Assert.NotNull(p1.NextCursor);

        var p2 = await svc.GetInvitationsAsync(org, camp.Id, null, null, p1.NextCursor, 2, default);
        Assert.Equal(new[] { "i2@x.com", "i3@x.com" }, p2.Items.Select(r => r.Email));

        var p3 = await svc.GetInvitationsAsync(org, camp.Id, null, null, p2.NextCursor, 2, default);
        Assert.Equal(new[] { "i4@x.com" }, p3.Items.Select(r => r.Email));
        Assert.Null(p3.NextCursor);   // trang cuối chưa đầy → hết
    }

    [Fact]
    public async Task TrungCreatedAt_TieBreakTheoId_KhongLapKhongMat()
    {
        using var tdb = new CampaignTestDb();
        var org = Guid.NewGuid();
        var camp = SeedCampaign(tdb, org);
        var same = DateTime.UtcNow.AddMinutes(-10);   // bulk invite = cùng transaction, cùng created_at
        SeedInvitation(tdb.Db, camp.Id, "a@x.com", createdAt: same);
        SeedInvitation(tdb.Db, camp.Id, "b@x.com", createdAt: same);
        SeedInvitation(tdb.Db, camp.Id, "c@x.com", createdAt: same);
        await tdb.Db.SaveChangesAsync();

        var svc = NewService(tdb.NewContext());
        var seen = new List<string>();
        string? cursor = null;

        for (var guard = 0; guard < 6; guard++)
        {
            var page = await svc.GetInvitationsAsync(org, camp.Id, null, null, cursor, 1, default);
            seen.AddRange(page.Items.Select(r => r.Email));
            cursor = page.NextCursor;
            if (cursor is null) break;
        }

        Assert.Equal(3, seen.Count);
        Assert.Equal(3, seen.Distinct().Count());
    }

    [Fact]
    public async Task CursorRac_VeTrangDau_KhongNo()
    {
        using var tdb = new CampaignTestDb();
        var org = Guid.NewGuid();
        var camp = SeedCampaign(tdb, org);
        SeedInvitation(tdb.Db, camp.Id, "a@x.com");
        await tdb.Db.SaveChangesAsync();

        var page = await NewService(tdb.NewContext())
            .GetInvitationsAsync(org, camp.Id, null, null, "khong-phai-base64!!", null, default);

        Assert.Single(page.Items);
    }

    [Fact]
    public async Task Limit_BiKepTran()
    {
        using var tdb = new CampaignTestDb();
        var org = Guid.NewGuid();
        var camp = SeedCampaign(tdb, org);
        SeedInvitation(tdb.Db, camp.Id, "a@x.com");
        await tdb.Db.SaveChangesAsync();

        var page = await NewService(tdb.NewContext())
            .GetInvitationsAsync(org, camp.Id, null, null, null, KeysetPaging.MaxLimit + 10_000, default);

        Assert.Single(page.Items);
        Assert.Null(page.NextCursor);
    }

    // ── search theo email ───────────────────────────────────────────────────────────────

    [Fact]
    public async Task Search_TheoEmail_KhongPhanBietHoaThuong_VaLocTruocPhanTrang()
    {
        using var tdb = new CampaignTestDb();
        var org = Guid.NewGuid();
        var camp = SeedCampaign(tdb, org);
        var t0 = DateTime.UtcNow;
        for (var i = 0; i < 8; i++)
            SeedInvitation(tdb.Db, camp.Id, $"noise{i}@other.com", createdAt: t0.AddMinutes(-i));
        // Dòng cần tìm nằm CUỐI theo thứ tự created_at → nếu lọc sau phân trang sẽ mất.
        SeedInvitation(tdb.Db, camp.Id, "Muc.Tieu@Corp.com", createdAt: t0.AddHours(-5));
        await tdb.Db.SaveChangesAsync();

        var page = await NewService(tdb.NewContext())
            .GetInvitationsAsync(org, camp.Id, null, "muc.tieu", null, 2, default);

        Assert.Equal("Muc.Tieu@Corp.com", Assert.Single(page.Items).Email);
    }

    // ── ?status= đẩy xuống SQL: từng nhánh ──────────────────────────────────────────────

    // Mỗi nhánh phải trả ĐÚNG tập của nó, và Status hiển thị (suy bằng ResolveDeliveryStatus) phải
    // KHỚP với nhánh SQL đã chọn — hai đường tính trạng thái không được phép lệch nhau.
    [Fact]
    public async Task Status_TungNhanh_TraDungTap_VaKhopVoiStatusHienThi()
    {
        using var tdb = new CampaignTestDb();
        var org = Guid.NewGuid();
        var camp = SeedCampaign(tdb, org);
        var now = DateTime.UtcNow;

        SeedInvitation(tdb.Db, camp.Id, "revoked@x.com", emailSentAt: now.AddDays(-1), revokedAt: now.AddHours(-1));
        SeedInvitation(tdb.Db, camp.Id, "expired@x.com", emailSentAt: now.AddDays(-9), expiresAt: now.AddDays(-1));
        SeedInvitation(tdb.Db, camp.Id, "sent@x.com", emailSentAt: now.AddHours(-2));
        SeedInvitation(tdb.Db, camp.Id, "queued@x.com");
        SeedInvitation(tdb.Db, camp.Id, "joined@x.com", emailSentAt: now.AddHours(-3));
        SeedMembership(tdb.Db, camp.Id, "joined@x.com", null, now.AddMinutes(-30));
        await tdb.Db.SaveChangesAsync();

        var svc = NewService(tdb.NewContext());

        foreach (var (want, email) in new[]
                 {
                     (InvitationDeliveryStatus.Revoked, "revoked@x.com"),
                     (InvitationDeliveryStatus.Joined,  "joined@x.com"),
                     (InvitationDeliveryStatus.Expired, "expired@x.com"),
                     (InvitationDeliveryStatus.Sent,    "sent@x.com"),
                     (InvitationDeliveryStatus.Queued,  "queued@x.com"),
                 })
        {
            var page = await svc.GetInvitationsAsync(org, camp.Id, want, null, null, null, default);
            var row = Assert.Single(page.Items);
            Assert.Equal(email, row.Email);
            Assert.Equal(want, row.Status);   // vị ngữ SQL và ResolveDeliveryStatus phải đồng ý
        }
    }

    // Nhánh Joined phải bắt CẢ 2 đường ghép: đường-2 theo cv_submission_id, đường-1 theo email
    // (case-insensitive — đường-1 chỉ Trim() còn đường-2 đã lowercase từ C13).
    [Fact]
    public async Task StatusJoined_BatCa2DuongGhep_EmailCaseInsensitive()
    {
        using var tdb = new CampaignTestDb();
        var org = Guid.NewGuid();
        var camp = SeedCampaign(tdb, org);
        var joinedAt = DateTime.UtcNow.AddHours(-2);

        var cvId = SeedCvSubmission(tdb.Db, camp.Id, "duong2@x.com").Id;
        SeedInvitation(tdb.Db, camp.Id, "Duong1@X.com", emailSentAt: DateTime.UtcNow);
        SeedInvitation(tdb.Db, camp.Id, "duong2@x.com", emailSentAt: DateTime.UtcNow, campaignCandidateId: cvId);
        SeedMembership(tdb.Db, camp.Id, "duong1@x.com", null, joinedAt);   // ghép theo email, khác hoa/thường
        SeedMembership(tdb.Db, camp.Id, null, cvId, joinedAt);             // ghép theo cv_submission
        await tdb.Db.SaveChangesAsync();

        var page = await NewService(tdb.NewContext())
            .GetInvitationsAsync(org, camp.Id, InvitationDeliveryStatus.Joined, null, null, null, default);

        Assert.Equal(2, page.Items.Count);
        Assert.All(page.Items, r => Assert.Equal(InvitationDeliveryStatus.Joined, r.Status));
        Assert.All(page.Items, r => Assert.NotNull(r.JoinedAt));
    }

    // 🔒 CA QUAN TRỌNG NHẤT: sau reissue (D4), lời mời CŨ cùng email phải nằm ở nhóm Revoked và
    // TUYỆT ĐỐI không lọt vào nhóm Joined của lời mời mới. Đây là lý do Revoked xếp trước Joined —
    // và khi đẩy vị ngữ xuống SQL thì thứ tự đó phải được diễn đạt lại cho đúng, không thì HR lọc
    // "ai đã join" sẽ thấy cả lời mời đã bị thu hồi.
    [Fact]
    public async Task SauReissue_LoiMoiCu_ThuocNhomRevoked_KhongLotVaoJoined()
    {
        using var tdb = new CampaignTestDb();
        var org = Guid.NewGuid();
        var camp = SeedCampaign(tdb, org);
        var email = "reissued@x.com";

        var old = SeedInvitation(tdb.Db, camp.Id, email,
            emailSentAt: DateTime.UtcNow.AddDays(-1), revokedAt: DateTime.UtcNow.AddHours(-3),
            createdAt: DateTime.UtcNow.AddDays(-1));
        var fresh = SeedInvitation(tdb.Db, camp.Id, email, emailSentAt: DateTime.UtcNow);
        SeedMembership(tdb.Db, camp.Id, email, null, DateTime.UtcNow.AddMinutes(-5));
        await tdb.Db.SaveChangesAsync();

        var svc = NewService(tdb.NewContext());

        var joined = await svc.GetInvitationsAsync(org, camp.Id, InvitationDeliveryStatus.Joined, null, null, null, default);
        Assert.Equal(fresh.Id, Assert.Single(joined.Items).Id);   // CHỈ lời mời mới

        var revoked = await svc.GetInvitationsAsync(org, camp.Id, InvitationDeliveryStatus.Revoked, null, null, null, default);
        Assert.Equal(old.Id, Assert.Single(revoked.Items).Id);    // lời mời cũ vẫn là Revoked
    }

    // Lời mời đã thu hồi mà CHƯA hết hạn/đã gửi mail cũng không được lọt vào Sent/Expired/Queued —
    // "không rơi vào bậc trên" là phần dễ quên nhất khi dịch chuỗi ưu tiên sang SQL.
    [Fact]
    public async Task Revoked_KhongLotVaoCacNhomBacDuoi()
    {
        using var tdb = new CampaignTestDb();
        var org = Guid.NewGuid();
        var camp = SeedCampaign(tdb, org);
        var now = DateTime.UtcNow;

        SeedInvitation(tdb.Db, camp.Id, "rv-sent@x.com", emailSentAt: now.AddHours(-1), revokedAt: now);
        SeedInvitation(tdb.Db, camp.Id, "rv-queued@x.com", revokedAt: now);
        SeedInvitation(tdb.Db, camp.Id, "rv-expired@x.com", revokedAt: now, expiresAt: now.AddDays(-1));
        await tdb.Db.SaveChangesAsync();

        var svc = NewService(tdb.NewContext());

        Assert.Empty((await svc.GetInvitationsAsync(org, camp.Id, InvitationDeliveryStatus.Sent, null, null, null, default)).Items);
        Assert.Empty((await svc.GetInvitationsAsync(org, camp.Id, InvitationDeliveryStatus.Queued, null, null, null, default)).Items);
        Assert.Empty((await svc.GetInvitationsAsync(org, camp.Id, InvitationDeliveryStatus.Expired, null, null, null, default)).Items);
        Assert.Equal(3, (await svc.GetInvitationsAsync(org, camp.Id, InvitationDeliveryStatus.Revoked, null, null, null, default)).Items.Count);
    }

    // Đã join thì không được đếm vào Expired/Sent/Queued (Joined ưu tiên cao hơn 3 bậc đó).
    [Fact]
    public async Task Joined_KhongLotVaoExpiredSentQueued()
    {
        using var tdb = new CampaignTestDb();
        var org = Guid.NewGuid();
        var camp = SeedCampaign(tdb, org);
        var now = DateTime.UtcNow;

        // Đã hết hạn NHƯNG đã join trước đó → Joined, KHÔNG phải Expired.
        SeedInvitation(tdb.Db, camp.Id, "joined-het-han@x.com",
            emailSentAt: now.AddDays(-9), expiresAt: now.AddDays(-1));
        SeedMembership(tdb.Db, camp.Id, "joined-het-han@x.com", null, now.AddDays(-3));
        await tdb.Db.SaveChangesAsync();

        var svc = NewService(tdb.NewContext());

        Assert.Empty((await svc.GetInvitationsAsync(org, camp.Id, InvitationDeliveryStatus.Expired, null, null, null, default)).Items);
        Assert.Single((await svc.GetInvitationsAsync(org, camp.Id, InvitationDeliveryStatus.Joined, null, null, null, default)).Items);
    }

    // Lọc status PHẢI chạy trước phân trang: 4 dòng Queued nằm rải rác giữa 8 dòng Sent, limit 2 →
    // nếu lọc sau phân trang thì trang đầu sẽ rỗng/thiếu.
    [Fact]
    public async Task Status_LocTruocPhanTrang_TrangDayDu()
    {
        using var tdb = new CampaignTestDb();
        var org = Guid.NewGuid();
        var camp = SeedCampaign(tdb, org);
        var t0 = DateTime.UtcNow;

        for (var i = 0; i < 8; i++)
            SeedInvitation(tdb.Db, camp.Id, $"sent{i}@x.com", emailSentAt: t0, createdAt: t0.AddMinutes(-i));
        for (var i = 0; i < 4; i++)
            SeedInvitation(tdb.Db, camp.Id, $"queued{i}@x.com", createdAt: t0.AddHours(-1).AddMinutes(-i));
        await tdb.Db.SaveChangesAsync();

        var svc = NewService(tdb.NewContext());

        var p1 = await svc.GetInvitationsAsync(org, camp.Id, InvitationDeliveryStatus.Queued, null, null, 2, default);
        Assert.Equal(2, p1.Items.Count);   // trang ĐẦY dù Queued nằm sau 8 dòng Sent
        Assert.All(p1.Items, r => Assert.Equal(InvitationDeliveryStatus.Queued, r.Status));

        var p2 = await svc.GetInvitationsAsync(org, camp.Id, InvitationDeliveryStatus.Queued, null, p1.NextCursor, 2, default);
        Assert.Equal(2, p2.Items.Count);

        // Trang vừa đầy vẫn phát cursor (keyset không biết trước là đã hết) → trang kế rỗng + hết cursor.
        // Đây là đánh đổi cố hữu của convention DB8, không phải lỗi: client dừng khi header vắng.
        var p3 = await svc.GetInvitationsAsync(org, camp.Id, InvitationDeliveryStatus.Queued, null, p2.NextCursor, 2, default);
        Assert.Empty(p3.Items);
        Assert.Null(p3.NextCursor);
    }

    // Giá trị status lạ → rỗng (giữ nguyên hành vi cũ: so chuỗi không khớp gì thì không ra dòng nào).
    [Fact]
    public async Task StatusLa_TraRong()
    {
        using var tdb = new CampaignTestDb();
        var org = Guid.NewGuid();
        var camp = SeedCampaign(tdb, org);
        SeedInvitation(tdb.Db, camp.Id, "a@x.com", emailSentAt: DateTime.UtcNow);
        await tdb.Db.SaveChangesAsync();

        var page = await NewService(tdb.NewContext())
            .GetInvitationsAsync(org, camp.Id, "KhongPhaiTrangThai", null, null, null, default);

        Assert.Empty(page.Items);
        Assert.Null(page.NextCursor);
    }

    // Membership của campaign KHÁC không được làm sai nhánh Joined (vị ngữ EXISTS phải khoá campaign_id).
    [Fact]
    public async Task MembershipCampaignKhac_KhongLamSaiNhanhJoined()
    {
        using var tdb = new CampaignTestDb();
        var org = Guid.NewGuid();
        var mine = SeedCampaign(tdb, org);
        var other = SeedCampaign(tdb, org);

        SeedInvitation(tdb.Db, mine.Id, "shared@x.com", emailSentAt: DateTime.UtcNow);
        SeedMembership(tdb.Db, other.Id, "shared@x.com", null, DateTime.UtcNow);
        await tdb.Db.SaveChangesAsync();

        var svc = NewService(tdb.NewContext());

        Assert.Empty((await svc.GetInvitationsAsync(org, mine.Id, InvitationDeliveryStatus.Joined, null, null, null, default)).Items);
        Assert.Single((await svc.GetInvitationsAsync(org, mine.Id, InvitationDeliveryStatus.Sent, null, null, null, default)).Items);
    }
}
