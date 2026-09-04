using Isas.CampaignService.DTOs;
using Isas.CampaignService.Models;
using Isas.CampaignService.Services;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging;
using Microsoft.AspNetCore.Http;
using Moq;

using CampaignSvc = Isas.CampaignService.Services.CampaignService;

namespace Isas.CampaignService.Tests;

/// <summary>
/// BK21 — trần <c>max_candidates</c> đếm NGƯỜI, không đếm ROW.
///
/// Ngữ nghĩa chốt 2026-08-08: "một người = một suất", tính bằng HỢP email distinct của
/// <c>campaign_invitations</c> (chưa revoke) và <c>cv_submission</c>, cộng số CV không tách được email.
///
/// Trước BK21 cả ba call site đều đếm row — kể cả đường mời, nơi <c>existingEmails</c> là
/// <c>List&lt;string&gt;</c> không <c>.Distinct()</c> (doc từng ghi nhầm là đã distinct). Hệ quả đo được
/// trên prod: một campaign cap=5 có 3 row sống nhưng chỉ 2 email ⇒ mất oan 1 suất.
/// </summary>
public class CampaignCapacityBk21Tests
{
    private static CampaignSvc NewService(CampaignDbContext db) =>
        new(db, Mock.Of<IFileService>(), Mock.Of<ILogger<CampaignSvc>>(), Mock.Of<IParserService>(),
            Mock.Of<ICriteriaSuggester>(), Mock.Of<IInvitationEmailPublisher>());

    private static CampaignInvitation NewInvitation(Guid campaignId, string email, DateTime? revokedAt = null) =>
        new()
        {
            Id = Guid.NewGuid(),
            CampaignId = campaignId,
            TokenHash = InvitationTokens.Hash(Guid.NewGuid().ToString()),
            ExpiresAt = DateTime.UtcNow.AddDays(7),
            Email = email,
            CreatedAt = DateTime.UtcNow,
            RevokedAt = revokedAt
        };

    private static CvSubmission NewCv(Guid campaignId, string? email,
        CvSubmissionStatus status = CvSubmissionStatus.Analyzed)
    {
        var now = DateTime.UtcNow;
        return new CvSubmission
        {
            Id = Guid.NewGuid(),
            CampaignId = campaignId,
            Email = email,
            CvParsedText = "cv text",
            CvFileUrl = $"campaigns/{campaignId}/candidates/x.pdf",
            ParseStatus = CvParseStatus.Done,
            Status = status,
            CreatedAt = now,
            UpdatedAt = now   // SQLite không có now(); default sql chỉ chạy trên Postgres
        };
    }

    // (a) Cùng một người có CẢ CV lẫn lời mời → chiếm ĐÚNG 1 suất, không phải 2.
    //     Đây là vế cốt lõi của "hợp hai bảng": trước BK21 hai đường đếm hai bảng rời nhau nên
    //     người này bị tính hai lần.
    [Fact]
    public async Task NguoiCoCaCvVaLoiMoi_ChiChiemMotSuat()
    {
        using var tdb = new CampaignTestDb();
        var owner = Guid.NewGuid();
        var camp = CampaignTestDb.NewCampaign(owner, CampaignStatus.Active);
        camp.MaxCandidates = 2;
        tdb.Db.Campaigns.Add(camp);
        tdb.Db.CvSubmissions.Add(NewCv(camp.Id, "dup@example.com"));
        tdb.Db.CampaignInvitations.Add(NewInvitation(camp.Id, "dup@example.com"));
        await tdb.Db.SaveChangesAsync();

        var svc = NewService(tdb.NewContext());

        // Chiếm 1 suất (không phải 2) ⇒ còn đúng 1 khe cho người mới.
        var ok = await svc.CreateInvitationsAsync(owner, owner, camp.Id,
            new List<string> { "new@example.com" }, default);
        Assert.Single(ok.Created);

        // Suất thứ 3 phải bị chặn.
        await Assert.ThrowsAsync<ArgumentException>(() =>
            svc.CreateInvitationsAsync(owner, owner, camp.Id,
                new List<string> { "third@example.com" }, default));
    }

    // (b) Mời một email ĐÃ có CV trong campaign → 0 suất mới (người đó đã chiếm chỗ từ lúc nộp CV).
    [Fact]
    public async Task MoiEmailDaCoCv_KhongTonSuatMoi()
    {
        using var tdb = new CampaignTestDb();
        var owner = Guid.NewGuid();
        var camp = CampaignTestDb.NewCampaign(owner, CampaignStatus.Active);
        camp.MaxCandidates = 1;
        tdb.Db.Campaigns.Add(camp);
        tdb.Db.CvSubmissions.Add(NewCv(camp.Id, "known@example.com"));
        await tdb.Db.SaveChangesAsync();

        var svc = NewService(tdb.NewContext());

        // Cap = 1 và đã có 1 người (qua CV). Mời CHÍNH người đó vẫn phải qua: 1 + 0 = 1 ≤ 1.
        var result = await svc.CreateInvitationsAsync(owner, owner, camp.Id,
            new List<string> { "known@example.com" }, default);

        Assert.Single(result.Created);
    }

    // (c) Hợp KHÔNG phân biệt hoa/thường. Hai bảng có quy ước khác nhau: invitation lưu nguyên như HR gõ
    //     (chỉ Trim), còn ExtractEmail luôn ToLowerInvariant ⇒ so sánh Ordinal sẽ đếm thành 2 người.
    [Fact]
    public async Task HopEmail_KhongPhanBietHoaThuong()
    {
        using var tdb = new CampaignTestDb();
        var owner = Guid.NewGuid();
        var camp = CampaignTestDb.NewCampaign(owner, CampaignStatus.Active);
        camp.MaxCandidates = 1;
        tdb.Db.Campaigns.Add(camp);
        tdb.Db.CvSubmissions.Add(NewCv(camp.Id, "alice@example.com"));            // CV: lowercase
        tdb.Db.CampaignInvitations.Add(NewInvitation(camp.Id, "Alice@Example.com")); // invitation: như HR gõ
        await tdb.Db.SaveChangesAsync();

        var svc = NewService(tdb.NewContext());

        // Cùng một người ⇒ 1 suất. Cap = 1 nên người MỚI phải bị chặn (nếu đếm thành 2 thì
        // đã vượt cap từ trước và thông điệp lỗi sẽ nói "hiện có 2").
        var ex = await Assert.ThrowsAsync<ArgumentException>(() =>
            svc.CreateInvitationsAsync(owner, owner, camp.Id,
                new List<string> { "bob@example.com" }, default));
        Assert.Contains("hiện có 1", ex.Message);
    }

    // (d) CV không tách được email → mỗi dòng tính MỘT suất (không dedup được nên không thể gộp).
    //     Thiếu vế này thì upload 1000 CV không email sẽ không bao giờ chạm trần.
    [Fact]
    public async Task CvKhongCoEmail_MoiDongMotSuat()
    {
        using var tdb = new CampaignTestDb();
        var owner = Guid.NewGuid();
        var camp = CampaignTestDb.NewCampaign(owner, CampaignStatus.Active);
        camp.MaxCandidates = 3;
        tdb.Db.Campaigns.Add(camp);
        tdb.Db.CvSubmissions.Add(NewCv(camp.Id, null));
        tdb.Db.CvSubmissions.Add(NewCv(camp.Id, null));
        await tdb.Db.SaveChangesAsync();

        var svc = NewService(tdb.NewContext());

        // 2 CV không email = 2 suất ⇒ còn 1 khe.
        Assert.Single((await svc.CreateInvitationsAsync(owner, owner, camp.Id,
            new List<string> { "one@example.com" }, default)).Created);

        var ex = await Assert.ThrowsAsync<ArgumentException>(() =>
            svc.CreateInvitationsAsync(owner, owner, camp.Id,
                new List<string> { "two@example.com" }, default));
        Assert.Contains("hiện có 3", ex.Message);
    }

    // (e) Lời mời ĐÃ revoke không chiếm suất — nếu tính cả revoked thì mỗi lần reissue lại ăn mất một suất.
    [Fact]
    public async Task LoiMoiDaRevoke_KhongChiemSuat()
    {
        using var tdb = new CampaignTestDb();
        var owner = Guid.NewGuid();
        var camp = CampaignTestDb.NewCampaign(owner, CampaignStatus.Active);
        camp.MaxCandidates = 1;
        tdb.Db.Campaigns.Add(camp);
        tdb.Db.CampaignInvitations.Add(NewInvitation(camp.Id, "gone@example.com", DateTime.UtcNow.AddHours(-1)));
        await tdb.Db.SaveChangesAsync();

        var svc = NewService(tdb.NewContext());

        var result = await svc.CreateInvitationsAsync(owner, owner, camp.Id,
            new List<string> { "fresh@example.com" }, default);

        Assert.Single(result.Created);
    }

    // (f) Reissue NHIỀU LẦN không ăn thêm suất. Đây là triệu chứng gốc của BK21: ReissueInvitationAsync
    //     dùng `old.RevokedAt ??= now` nên phát lại một lời mời ĐÃ revoke vẫn Add row mới ⇒ số row sống
    //     cùng email tăng dần. Đếm row thì mỗi lần reissue ăn mất một suất của người khác.
    [Fact]
    public async Task ReissueNhieuLan_KhongAnThemSuat()
    {
        using var tdb = new CampaignTestDb();
        var owner = Guid.NewGuid();
        var camp = CampaignTestDb.NewCampaign(owner, CampaignStatus.Active);
        camp.MaxCandidates = 2;
        tdb.Db.Campaigns.Add(camp);
        var first = NewInvitation(camp.Id, "reissued@example.com");
        tdb.Db.CampaignInvitations.Add(first);
        await tdb.Db.SaveChangesAsync();

        var svc = NewService(tdb.NewContext());

        // Phát lại CÙNG một invitation gốc hai lần → sinh thêm 2 row sống cùng email.
        await svc.ReissueInvitationAsync(owner, owner, camp.Id, first.Id, default);
        await svc.ReissueInvitationAsync(owner, owner, camp.Id, first.Id, default);

        using var check = tdb.NewContext();
        var live = await check.CampaignInvitations
            .Where(i => i.CampaignId == camp.Id && i.RevokedAt == null).ToListAsync();
        Assert.True(live.Count > 1, "tiền đề của test: reissue phải sinh nhiều row sống cùng email");
        Assert.Single(live.Select(i => i.Email).Distinct(StringComparer.OrdinalIgnoreCase));

        // Vẫn chỉ là MỘT người ⇒ cap = 2 còn đúng 1 khe cho người mới.
        var ok = await svc.CreateInvitationsAsync(owner, owner, camp.Id,
            new List<string> { "second@example.com" }, default);
        Assert.Single(ok.Created);
    }

    // (g) Mời ứng viên shortlist (đường 2) tốn 0 suất mới — họ đã có CV nên đã chiếm chỗ.
    [Fact]
    public async Task MoiUngVienShortlist_TonKhongSuatMoi()
    {
        using var tdb = new CampaignTestDb();
        var owner = Guid.NewGuid();
        var camp = CampaignTestDb.NewCampaign(owner, CampaignStatus.Active);
        camp.MaxCandidates = 2;
        tdb.Db.Campaigns.Add(camp);
        var a = NewCv(camp.Id, "sl-a@example.com");
        var b = NewCv(camp.Id, "sl-b@example.com");
        tdb.Db.CvSubmissions.AddRange(a, b);
        await tdb.Db.SaveChangesAsync();

        var svc = NewService(tdb.NewContext());

        // 2 CV = 2 suất = đúng cap. Mời chính 2 người đó phải QUA (0 suất mới).
        // Đếm row invitation như trước BK21 sẽ ra 0 + 2 > 2 → chặn oan.
        var result = await svc.InviteShortlistedCandidatesAsync(
            owner, owner, camp.Id, new List<Guid> { a.Id, b.Id }, includeIneligible: false, default);

        Assert.Equal(2, result.Invited.Count);
        Assert.Empty(result.Failed);
    }

    // (h) ⭐ Phép PHÂN BIỆT của vế HỢP: người chỉ mới nộp CV, CHƯA được mời và KHÔNG nằm trong batch,
    //     vẫn chiếm một suất.
    //
    //     Các test (a)/(b)/(g) ở trên KHÔNG phân biệt được — mutation "bỏ union.UnionWith(cvEmails)"
    //     chạy qua chúng XANH hết. Lý do: ở những ca đó vế hợp chỉ CHUYỂN người giữa `occupied` và
    //     `newSeats`, còn TỔNG thì y nguyên. Hợp chỉ quan sát được khi có người trong `cv_submission`
    //     không xuất hiện ở cả hai chỗ kia — lúc đó bỏ hợp làm tổng TỤT xuống và cap nới lỏng thầm lặng.
    [Fact]
    public async Task CvChuaDuocMoi_VanChiemSuat()
    {
        using var tdb = new CampaignTestDb();
        var owner = Guid.NewGuid();
        var camp = CampaignTestDb.NewCampaign(owner, CampaignStatus.Active);
        camp.MaxCandidates = 1;
        tdb.Db.Campaigns.Add(camp);
        // ghost@ chỉ có CV: chưa từng được mời, và KHÔNG nằm trong batch mời bên dưới.
        tdb.Db.CvSubmissions.Add(NewCv(camp.Id, "ghost@example.com"));
        await tdb.Db.SaveChangesAsync();

        var svc = NewService(tdb.NewContext());

        // Cap = 1 và ghost@ đã chiếm suất đó ⇒ mời người khác phải bị chặn.
        var ex = await Assert.ThrowsAsync<ArgumentException>(() =>
            svc.CreateInvitationsAsync(owner, owner, camp.Id,
                new List<string> { "other@example.com" }, default));
        Assert.Contains("hiện có 1", ex.Message);

        using var check = tdb.NewContext();
        Assert.Empty(await check.CampaignInvitations.Where(i => i.CampaignId == camp.Id).ToListAsync());
    }

    // (i) ⭐ Đường upload CV cũng đếm NGƯỜI: lời mời đã phát chiếm suất, nên upload CV bị chặn.
    //     Trước BK21 đường này chỉ đếm row `cv_submission` nên hoàn toàn mù với lời mời đã gửi —
    //     cùng một campaign có thể nhận cap người qua đường mời RỒI lại nhận thêm cap người nữa qua CV.
    [Fact]
    public async Task UploadCv_DemCaLoiMoiDaPhat()
    {
        using var tdb = new CampaignTestDb();
        var owner = Guid.NewGuid();
        var camp = CampaignTestDb.NewCampaign(owner, CampaignStatus.Active);
        camp.MaxCandidates = 1;
        tdb.Db.Campaigns.Add(camp);
        tdb.Db.CampaignInvitations.Add(NewInvitation(camp.Id, "invited@example.com"));
        await tdb.Db.SaveChangesAsync();

        var parser = new Mock<IParserService>();
        parser.Setup(p => p.ParseAsync(It.IsAny<Stream>(), It.IsAny<CancellationToken>()))
              .ReturnsAsync(new ParseResult { RawText = "cv text someone@example.com" });
        var svc = new CampaignSvc(tdb.NewContext(), Mock.Of<IFileService>(),
            Mock.Of<ILogger<CampaignSvc>>(), parser.Object,
            Mock.Of<ICriteriaSuggester>(), Mock.Of<IInvitationEmailPublisher>());

        var pdf = new MemoryStream(System.Text.Encoding.UTF8.GetBytes("xxxxxxxx"));
        var files = new FormFileCollection
        {
            new FormFile(pdf, 0, pdf.Length, "files", "cv.pdf")
            {
                Headers = new HeaderDictionary(),
                ContentType = "application/pdf"
            }
        };

        await Assert.ThrowsAsync<ArgumentException>(() =>
            svc.ScreenCandidatesAsync(owner, owner, camp.Id, files, default));

        using var check = tdb.NewContext();
        Assert.Empty(await check.CvSubmissions.Where(c => c.CampaignId == camp.Id).ToListAsync());
    }
}
