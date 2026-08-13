using System.Security.Claims;
using System.Text;
using CsvHelper;
using Isas.CampaignService.Controllers;
using Isas.CampaignService.DTOs;
using Isas.CampaignService.Models;
using Isas.CampaignService.Services;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;
using Moq;

// 'CampaignService' vừa là namespace vừa là tên class → alias cho rõ ràng.
using CampaignSvc = Isas.CampaignService.Services.CampaignService;

namespace Isas.CampaignService.Tests;

/// <summary>
/// FX1 — quan hệ THẬT <c>campaign_membership.invitation_id</c> → <c>campaign_invitations.id</c>.
///
/// Bối cảnh: DB16 tách bảng God <c>campaign_candidates</c> thành <c>cv_submission</c> +
/// <c>campaign_membership</c> nhưng KHÔNG dựng lại khoá nối sang <c>campaign_invitations</c>. Vì thiếu
/// khoá, đường đọc <c>GetInvitationsAsync</c> phải GHÉP BẰNG EMAIL — suy đoán, sai khi cùng một email
/// được mời nhiều lần trong 1 campaign.
///
/// Nhóm test này khoá 3 hợp đồng:
///  (A) CẢ HAI nhánh của <c>JoinCampaignAsync</c> (tạo mới + idempotent) đều set <c>invitation_id</c>;
///  (B) đường đọc ghép theo quan hệ thật, và membership ĐÃ có link thì KHÔNG còn bị ghép bằng email;
///  (C) fallback lịch sử (membership chưa có link) vẫn hoạt động — không regress hành vi cũ.
/// Kèm 1 test end-to-end join → export CSV cho CẢ hai đường (đường-1 mời-thẳng, đường-2 shortlist), để
/// việc thu độ dài cột / giữ snapshot F5 không âm thầm làm hỏng file HR tải về.
/// </summary>
public class MembershipInvitationLinkFx1Tests
{
    private static readonly Guid FixedCandidate = Guid.Parse("aaaaaaaa-0000-0000-0000-000000000001");

    // ── Dựng service ────────────────────────────────────────────────────────────────────────
    private static ParticipationService NewParticipation(CampaignDbContext db, Guid? candidateId = null)
    {
        var auth = new Mock<IAuthProvisionClient>();
        auth.Setup(x => x.ProvisionCandidateAsync(It.IsAny<string>(), It.IsAny<string?>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(new ProvisionedCandidate(candidateId ?? FixedCandidate, "jwt"));

        var session = new Mock<ICampaignSessionClient>();
        session.Setup(x => x.CreateOrGetSessionAsync(
                It.IsAny<Guid>(), It.IsAny<Guid>(), It.IsAny<Guid>(), It.IsAny<string>(),
                It.IsAny<IReadOnlyList<string>>(), It.IsAny<IReadOnlyList<SessionCriterionInput>>(),
                It.IsAny<DateTime?>(), It.IsAny<bool?>(), It.IsAny<int?>(), It.IsAny<int?>(), It.IsAny<int?>(), It.IsAny<string>(), It.IsAny<IReadOnlyList<SessionQuestionInput>?>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(new CampaignSessionResult(Guid.NewGuid(), new List<SessionQuestion>()));

        return new ParticipationService(db, auth.Object, session.Object, NullLogger<ParticipationService>.Instance);
    }

    private static CampaignSvc NewCampaignSvc(CampaignDbContext db) =>
        new(db, Mock.Of<IFileService>(), Mock.Of<ILogger<CampaignSvc>>(), Mock.Of<IParserService>(),
            Mock.Of<ICriteriaSuggester>(), Mock.Of<IInvitationEmailPublisher>());

    private static CampaignController NewController(CampaignDbContext db, Guid orgId)
    {
        var controller = new CampaignController(
            NewCampaignSvc(db), Mock.Of<ICvScreeningService>(), Mock.Of<ILogger<CampaignController>>());
        var identity = new ClaimsIdentity(new[]
        {
            new Claim("org_id", orgId.ToString()),
            new Claim(ClaimTypes.NameIdentifier, Guid.NewGuid().ToString()),
        }, "Test");
        controller.ControllerContext = new ControllerContext
        {
            HttpContext = new DefaultHttpContext { User = new ClaimsPrincipal(identity) }
        };
        return controller;
    }

    // ── Seed ────────────────────────────────────────────────────────────────────────────────
    // DB23 — DB giữ HASH, ứng viên cầm token thô: dẫn xuất deterministic từ Id để test gọi được như thật.
    private static string RawTokenOf(CampaignInvitation inv) => inv.Id.ToString("N");

    private static CampaignInvitation SeedInvitation(
        CampaignDbContext db, Guid campaignId, string email,
        Guid? campaignCandidateId = null, DateTime? revokedAt = null, DateTime? emailSentAt = null,
        Guid? slotId = null)
    {
        var id = Guid.NewGuid();
        var inv = new CampaignInvitation
        {
            Id = id,
            CampaignId = campaignId,
            CampaignCandidateId = campaignCandidateId,
            SlotId = slotId,
            TokenHash = InvitationTokens.Hash(id.ToString("N")),
            Email = email,
            ExpiresAt = DateTime.UtcNow.AddDays(7),
            SentAt = DateTime.UtcNow,
            EmailSentAt = emailSentAt ?? DateTime.UtcNow,
            RevokedAt = revokedAt,
            CreatedAt = DateTime.UtcNow
        };
        db.CampaignInvitations.Add(inv);
        return inv;
    }

    private static CvSubmission SeedCv(CampaignDbContext db, Guid campaignId, string email, string fullName)
    {
        var cv = new CvSubmission
        {
            Id = Guid.NewGuid(),
            CampaignId = campaignId,
            Email = email,
            FullName = fullName,
            ParseStatus = CvParseStatus.Done,
            Status = CvSubmissionStatus.Invited,
            CreatedAt = DateTime.UtcNow,
            UpdatedAt = DateTime.UtcNow
        };
        db.CvSubmissions.Add(cv);
        return cv;
    }

    // ══════════════════════════════════════════════════════════════════════════════════════
    // (A) CẢ HAI nhánh JoinCampaignAsync đều set invitation_id
    // ══════════════════════════════════════════════════════════════════════════════════════

    // Nhánh TẠO MỚI, đường-1 (mời thẳng email, không CV).
    [Fact]
    public async Task Join_NhanhTaoMoi_Duong1_SetInvitationId()
    {
        using var tdb = new CampaignTestDb();
        var camp = CampaignTestDb.NewCampaign(Guid.NewGuid(), CampaignStatus.Active);
        tdb.Db.Campaigns.Add(camp);
        var inv = SeedInvitation(tdb.Db, camp.Id, "fx1-new@acme.test");
        await tdb.Db.SaveChangesAsync();

        await NewParticipation(tdb.NewContext()).JoinCampaignAsync(RawTokenOf(inv), inv.Email, default);

        using var check = tdb.NewContext();
        var m = await check.CampaignMemberships.SingleAsync(x => x.CampaignId == camp.Id);
        Assert.Equal(inv.Id, m.InvitationId);
    }

    // Nhánh TẠO MỚI, đường-2 (shortlist): giữ CẢ cv_submission_id lẫn invitation_id.
    [Fact]
    public async Task Join_NhanhTaoMoi_Duong2_SetCaInvitationIdVaCvSubmissionId()
    {
        using var tdb = new CampaignTestDb();
        var camp = CampaignTestDb.NewCampaign(Guid.NewGuid(), CampaignStatus.Active);
        tdb.Db.Campaigns.Add(camp);
        var cv = SeedCv(tdb.Db, camp.Id, "fx1-cv@acme.test", "Lê Thị B");
        var inv = SeedInvitation(tdb.Db, camp.Id, "fx1-cv@acme.test", campaignCandidateId: cv.Id);
        await tdb.Db.SaveChangesAsync();

        await NewParticipation(tdb.NewContext()).JoinCampaignAsync(RawTokenOf(inv), inv.Email, default);

        using var check = tdb.NewContext();
        var m = await check.CampaignMemberships.SingleAsync(x => x.CampaignId == camp.Id);
        Assert.Equal(inv.Id, m.InvitationId);
        Assert.Equal(cv.Id, m.CvSubmissionId);
    }

    // 🔴 Nhánh IDEMPOTENT — chỗ dễ quên nhất (F5 từng suýt sót): membership đã tồn tại từ TRƯỚC FX1
    // (invitation_id null) → join lại PHẢI điền link, không thì row lịch sử vĩnh viễn không có quan hệ.
    [Fact]
    public async Task Join_NhanhIdempotent_MembershipCuChuaCoLink_DuocDienInvitationId()
    {
        using var tdb = new CampaignTestDb();
        var camp = CampaignTestDb.NewCampaign(Guid.NewGuid(), CampaignStatus.Active);
        tdb.Db.Campaigns.Add(camp);
        var inv = SeedInvitation(tdb.Db, camp.Id, "fx1-old@acme.test");
        tdb.Db.CampaignMemberships.Add(new CampaignMembership
        {
            Id = Guid.NewGuid(),
            CampaignId = camp.Id,
            CandidateId = FixedCandidate,
            InvitationId = null,           // membership "lịch sử"
            Status = MembershipStatus.Joined,
            JoinedAt = DateTime.UtcNow,
            CreatedAt = DateTime.UtcNow,
            UpdatedAt = DateTime.UtcNow
        });
        await tdb.Db.SaveChangesAsync();

        await NewParticipation(tdb.NewContext()).JoinCampaignAsync(RawTokenOf(inv), inv.Email, default);

        using var check = tdb.NewContext();
        var m = await check.CampaignMemberships.SingleAsync(x => x.CampaignId == camp.Id);
        Assert.Equal(inv.Id, m.InvitationId);
    }

    // Nhánh idempotent GHI ĐÈ bằng lời mời MỚI (reissue D4), không giữ lời mời cũ: sau reissue, lời mời
    // còn hiệu lực mới là cái phản ánh đúng "ứng viên vào bằng đường nào".
    [Fact]
    public async Task Join_SauReissue_InvitationIdTroVeLoiMoiMoi()
    {
        using var tdb = new CampaignTestDb();
        var camp = CampaignTestDb.NewCampaign(Guid.NewGuid(), CampaignStatus.Active);
        tdb.Db.Campaigns.Add(camp);
        var old = SeedInvitation(tdb.Db, camp.Id, "fx1-reissue@acme.test");
        await tdb.Db.SaveChangesAsync();

        await NewParticipation(tdb.NewContext()).JoinCampaignAsync(RawTokenOf(old), old.Email, default);

        // Reissue: thu hồi lời mời cũ, phát lời mời mới cùng email.
        using (var ctx = tdb.NewContext())
        {
            var o = await ctx.CampaignInvitations.SingleAsync(x => x.Id == old.Id);
            o.RevokedAt = DateTime.UtcNow;
            await ctx.SaveChangesAsync();
        }
        CampaignInvitation fresh;
        using (var ctx = tdb.NewContext())
        {
            fresh = SeedInvitation(ctx, camp.Id, "fx1-reissue@acme.test");
            await ctx.SaveChangesAsync();
        }

        await NewParticipation(tdb.NewContext()).JoinCampaignAsync(RawTokenOf(fresh), fresh.Email, default);

        using var check = tdb.NewContext();
        var m = await check.CampaignMemberships.SingleAsync(x => x.CampaignId == camp.Id);
        Assert.Equal(fresh.Id, m.InvitationId);   // KHÔNG còn trỏ lời mời đã thu hồi
        Assert.Single(await check.CampaignMemberships.Where(x => x.CampaignId == camp.Id).ToListAsync());
    }

    // QUAN HỆ THẬT, không phải "một cột Guid nữa": DB9 đã chứng minh SQLite CÓ enforce FK (EF10), nên
    // trỏ vào lời mời không tồn tại phải bị DB từ chối. Thiếu test này thì mọi test còn lại vẫn xanh y
    // nguyên kể cả khi `invitation_id` chỉ là scalar rời — tức là không khoá được thứ task này làm.
    [Fact]
    public async Task InvitationIdTroVaoLoiMoiKhongTonTai_BiFkTuChoi()
    {
        using var tdb = new CampaignTestDb();
        var camp = CampaignTestDb.NewCampaign(Guid.NewGuid(), CampaignStatus.Active);
        tdb.Db.Campaigns.Add(camp);
        await tdb.Db.SaveChangesAsync();

        using var ctx = tdb.NewContext();
        ctx.CampaignMemberships.Add(new CampaignMembership
        {
            Id = Guid.NewGuid(),
            CampaignId = camp.Id,
            CandidateId = Guid.NewGuid(),
            InvitationId = Guid.NewGuid(),   // id ma
            Status = MembershipStatus.Joined,
            JoinedAt = DateTime.UtcNow,
            CreatedAt = DateTime.UtcNow,
            UpdatedAt = DateTime.UtcNow
        });

        await Assert.ThrowsAnyAsync<DbUpdateException>(() => ctx.SaveChangesAsync());
    }

    // ══════════════════════════════════════════════════════════════════════════════════════
    // (A2) Q7 — join phải CHÉP khung giờ từ lời mời sang membership
    //
    // Trước Q7 `campaign_membership.slot_id` được ĐỌC ở 4 chỗ (guard khung giờ lúc Start ·
    // StartedCount mỗi slot · guard không-xoá-slot-đang-thi) mà KHÔNG đường ghi nào chạm tới —
    // khung giờ chỉ nằm trên campaign_invitations. Cột luôn NULL nên cả 3 tính năng đều chết im.
    // ⚠ Lý do bug sống sót: mọi test slot hiện có (ParticipationServiceTests, CampaignSlotServiceTests)
    // đều TỰ TAY set membership.SlotId rồi mới test Start/Delete ⇒ verify guard nhưng mù hoàn toàn
    // việc guard không bao giờ nhận được dữ liệu. Các test dưới đi qua ĐÚNG đường ghi (join thật).
    // ══════════════════════════════════════════════════════════════════════════════════════

    private static CampaignSlot SeedSlot(
        CampaignDbContext db, Guid campaignId, DateTime startsAt, int capacity = 5)
    {
        var slot = new CampaignSlot
        {
            Id = Guid.NewGuid(),
            CampaignId = campaignId,
            StartsAt = startsAt,
            EndsAt = startsAt.AddHours(1),
            Capacity = capacity
        };
        db.CampaignSlots.Add(slot);
        return slot;
    }

    // Nhánh TẠO MỚI — membership sinh ra đã mang khung giờ của lời mời.
    [Fact]
    public async Task Q7_Join_NhanhTaoMoi_ChepSlotIdTuLoiMoi()
    {
        using var tdb = new CampaignTestDb();
        var camp = CampaignTestDb.NewCampaign(Guid.NewGuid(), CampaignStatus.Active);
        tdb.Db.Campaigns.Add(camp);
        var slot = SeedSlot(tdb.Db, camp.Id, DateTime.UtcNow.AddDays(1));
        var inv = SeedInvitation(tdb.Db, camp.Id, "q7-new@acme.test", slotId: slot.Id);
        await tdb.Db.SaveChangesAsync();

        await NewParticipation(tdb.NewContext()).JoinCampaignAsync(RawTokenOf(inv), inv.Email, default);

        using var check = tdb.NewContext();
        var m = await check.CampaignMemberships.SingleAsync(x => x.CampaignId == camp.Id);
        Assert.Equal(slot.Id, m.SlotId);
    }

    // Nhánh IDEMPOTENT — membership có từ trước Q7 (slot_id null) join lại PHẢI được điền, không thì
    // row lịch sử vĩnh viễn không có khung giờ và guard Start không bao giờ chạy cho họ.
    [Fact]
    public async Task Q7_Join_NhanhIdempotent_MembershipCu_DuocDienSlotId()
    {
        using var tdb = new CampaignTestDb();
        var camp = CampaignTestDb.NewCampaign(Guid.NewGuid(), CampaignStatus.Active);
        tdb.Db.Campaigns.Add(camp);
        var slot = SeedSlot(tdb.Db, camp.Id, DateTime.UtcNow.AddDays(1));
        var inv = SeedInvitation(tdb.Db, camp.Id, "q7-old@acme.test", slotId: slot.Id);
        tdb.Db.CampaignMemberships.Add(new CampaignMembership
        {
            Id = Guid.NewGuid(),
            CampaignId = camp.Id,
            CandidateId = FixedCandidate,
            SlotId = null,                 // membership "lịch sử"
            Status = MembershipStatus.Joined,
            JoinedAt = DateTime.UtcNow,
            CreatedAt = DateTime.UtcNow,
            UpdatedAt = DateTime.UtcNow
        });
        await tdb.Db.SaveChangesAsync();

        await NewParticipation(tdb.NewContext()).JoinCampaignAsync(RawTokenOf(inv), inv.Email, default);

        using var check = tdb.NewContext();
        var m = await check.CampaignMemberships.SingleAsync(x => x.CampaignId == camp.Id);
        Assert.Equal(slot.Id, m.SlotId);
    }

    // 🔴 Đây là ca phân biệt `=` với `??=`: HR dời ứng viên sang khung giờ khác rồi phát lại lời mời
    // (D4). Với `??=` membership đóng băng ở slot CŨ ⇒ ứng viên tới đúng giờ mới vẫn bị chặn ngoài
    // khung, và nếu slot cũ đã bị xoá thì Start ném thẳng "Không tìm thấy khung giờ đã được phân".
    [Fact]
    public async Task Q7_Join_SauReissueDoiSlot_SlotIdTheoLoiMoiMoi()
    {
        using var tdb = new CampaignTestDb();
        var camp = CampaignTestDb.NewCampaign(Guid.NewGuid(), CampaignStatus.Active);
        tdb.Db.Campaigns.Add(camp);
        var slotCu = SeedSlot(tdb.Db, camp.Id, DateTime.UtcNow.AddDays(1));
        var slotMoi = SeedSlot(tdb.Db, camp.Id, DateTime.UtcNow.AddDays(2));
        var old = SeedInvitation(tdb.Db, camp.Id, "q7-reissue@acme.test", slotId: slotCu.Id);
        await tdb.Db.SaveChangesAsync();

        await NewParticipation(tdb.NewContext()).JoinCampaignAsync(RawTokenOf(old), old.Email, default);

        // Reissue: thu hồi lời mời cũ, phát lời mời mới ở khung giờ KHÁC.
        using (var ctx = tdb.NewContext())
        {
            (await ctx.CampaignInvitations.SingleAsync(x => x.Id == old.Id)).RevokedAt = DateTime.UtcNow;
            await ctx.SaveChangesAsync();
        }
        CampaignInvitation fresh;
        using (var ctx = tdb.NewContext())
        {
            fresh = SeedInvitation(ctx, camp.Id, "q7-reissue@acme.test", slotId: slotMoi.Id);
            await ctx.SaveChangesAsync();
        }

        await NewParticipation(tdb.NewContext()).JoinCampaignAsync(RawTokenOf(fresh), fresh.Email, default);

        using var check = tdb.NewContext();
        var m = await check.CampaignMemberships.SingleAsync(x => x.CampaignId == camp.Id);
        Assert.Equal(slotMoi.Id, m.SlotId);
    }

    // 🔴 End-to-end join → Start: chứng minh khung giờ THẬT SỰ được thực thi, không chỉ "cột có giá trị".
    // Đây đúng hành vi đang hỏng trên deploy: ứng viên có khung giờ đã đóng 4 tiếng bấm Start vẫn 200,
    // trừ credit org thật, deadline rơi về campaign.ExpiresAt.
    [Fact]
    public async Task Q7_JoinRoiStart_SlotDaDong_BiChanNgoaiKhung()
    {
        using var tdb = new CampaignTestDb();
        var camp = ActiveCampaignReadyForStart(tdb);
        var slot = SeedSlot(tdb.Db, camp.Id, DateTime.UtcNow.AddHours(-5));   // đã kết thúc 4 tiếng trước
        var inv = SeedInvitation(tdb.Db, camp.Id, "q7-closed@acme.test", slotId: slot.Id);
        await tdb.Db.SaveChangesAsync();

        await NewParticipation(tdb.NewContext()).JoinCampaignAsync(RawTokenOf(inv), inv.Email, default);

        await Assert.ThrowsAsync<OutsideSlotWindowException>(() =>
            NewParticipation(tdb.NewContext()).StartInterviewAsync(FixedCandidate, camp.Id, default));
    }

    // Vế còn lại: khung giờ ĐANG mở → Start được, và deadline lấy mốc SỚM HƠN (slot.EndsAt) chứ không
    // phải campaign.ExpiresAt — cũng chỉ có nghĩa khi membership.SlotId thật sự được ghi lúc join.
    [Fact]
    public async Task Q7_JoinRoiStart_SlotDangMo_DeadlineTheoSlot()
    {
        using var tdb = new CampaignTestDb();
        var camp = ActiveCampaignReadyForStart(tdb);
        camp.ExpiresAt = DateTime.UtcNow.AddDays(3);           // campaign còn hạn rất dài
        var slot = SeedSlot(tdb.Db, camp.Id, DateTime.UtcNow.AddMinutes(-5));   // đang mở, hết sau ~55'
        var inv = SeedInvitation(tdb.Db, camp.Id, "q7-open@acme.test", slotId: slot.Id);
        await tdb.Db.SaveChangesAsync();

        await NewParticipation(tdb.NewContext()).JoinCampaignAsync(RawTokenOf(inv), inv.Email, default);
        var res = await NewParticipation(tdb.NewContext()).StartInterviewAsync(FixedCandidate, camp.Id, default);

        Assert.Equal(slot.EndsAt, res.DeadlineAt);
        using var check = tdb.NewContext();
        var m = await check.CampaignMemberships.SingleAsync(x => x.CampaignId == camp.Id);
        Assert.Equal(slot.EndsAt, m.InterviewDeadlineAt);
        Assert.Equal(slot.Id, m.SlotId);
    }

    private static Campaign ActiveCampaignReadyForStart(CampaignTestDb tdb)
    {
        var camp = CampaignTestDb.NewCampaign(Guid.NewGuid(), CampaignStatus.Active);
        camp.Domain = "BE";
        camp.Questions.Add(new CampaignQuestion
        {
            Id = Guid.NewGuid(), CampaignId = camp.Id, OrgId = camp.OrgId,
            QuestionText = "Giải thích DI?", Source = QuestionSource.CustomHr,
            IsRequired = true, CreatedAt = DateTime.UtcNow
        });
        camp.Criteria.Add(new CampaignCriterion
        {
            Id = Guid.NewGuid(), CampaignId = camp.Id, OrderNo = 0, Name = "Communication",
            Weight = 1.0m, MaxScore = 5, Source = CriterionSource.HrEdited,
            CreatedAt = DateTime.UtcNow, UpdatedAt = DateTime.UtcNow
        });
        tdb.Db.Campaigns.Add(camp);
        return camp;
    }

    // ══════════════════════════════════════════════════════════════════════════════════════
    // (B) Đường đọc ghép theo QUAN HỆ THẬT
    // ══════════════════════════════════════════════════════════════════════════════════════

    // Joined suy ra từ invitation_id — không cần email khớp gì cả.
    [Fact]
    public async Task DanhSachLoiMoi_GhepTheoInvitationId_HienJoined()
    {
        using var tdb = new CampaignTestDb();
        var org = Guid.NewGuid();
        var camp = CampaignTestDb.NewCampaign(org, CampaignStatus.Active);
        tdb.Db.Campaigns.Add(camp);
        var inv = SeedInvitation(tdb.Db, camp.Id, "fx1-join@acme.test");
        await tdb.Db.SaveChangesAsync();

        await NewParticipation(tdb.NewContext()).JoinCampaignAsync(RawTokenOf(inv), inv.Email, default);

        var rows = (await NewCampaignSvc(tdb.NewContext())
            .GetInvitationsAsync(org, camp.Id, null, null, null, null, default)).Items;

        var row = Assert.Single(rows);
        Assert.Equal(InvitationDeliveryStatus.Joined, row.Status);
        Assert.NotNull(row.JoinedAt);
    }

    // 🔴 ĐÂY là lỗ mà quan hệ này sinh ra để bịt: HAI lời mời cùng email trong 1 campaign, ứng viên mới
    // join bằng lời mời THỨ NHẤT. Ghép bằng email sẽ báo CẢ HAI là "Joined" ⇒ HR tưởng lời mời thứ hai
    // đã được dùng và không gửi lại. Ghép theo invitation_id thì chỉ đúng lời mời đã dùng là Joined.
    [Fact]
    public async Task DanhSachLoiMoi_HaiLoiMoiCungEmail_ChiCaiDaDungLaJoined()
    {
        using var tdb = new CampaignTestDb();
        var org = Guid.NewGuid();
        var camp = CampaignTestDb.NewCampaign(org, CampaignStatus.Active);
        tdb.Db.Campaigns.Add(camp);
        var used = SeedInvitation(tdb.Db, camp.Id, "dup@acme.test");
        var unused = SeedInvitation(tdb.Db, camp.Id, "dup@acme.test");
        await tdb.Db.SaveChangesAsync();

        await NewParticipation(tdb.NewContext()).JoinCampaignAsync(RawTokenOf(used), used.Email, default);

        var rows = (await NewCampaignSvc(tdb.NewContext())
            .GetInvitationsAsync(org, camp.Id, null, null, null, null, default)).Items;

        Assert.Equal(2, rows.Count);
        Assert.Equal(InvitationDeliveryStatus.Joined, rows.Single(r => r.Id == used.Id).Status);
        Assert.NotEqual(InvitationDeliveryStatus.Joined, rows.Single(r => r.Id == unused.Id).Status);
    }

    // Cùng hợp đồng nhưng ở đường LỌC (?status=) — vị ngữ SQL phải khớp lời suy read-time, không thì
    // bảng hiện "Sent" mà bộ lọc "Sent" lại không trả về nó.
    [Fact]
    public async Task LocTheoStatus_HaiLoiMoiCungEmail_KhongLanTrangThaiJoined()
    {
        using var tdb = new CampaignTestDb();
        var org = Guid.NewGuid();
        var camp = CampaignTestDb.NewCampaign(org, CampaignStatus.Active);
        tdb.Db.Campaigns.Add(camp);
        var used = SeedInvitation(tdb.Db, camp.Id, "dup2@acme.test");
        var unused = SeedInvitation(tdb.Db, camp.Id, "dup2@acme.test");
        await tdb.Db.SaveChangesAsync();

        await NewParticipation(tdb.NewContext()).JoinCampaignAsync(RawTokenOf(used), used.Email, default);

        var svc = NewCampaignSvc(tdb.NewContext());
        var joined = (await svc.GetInvitationsAsync(org, camp.Id, "Joined", null, null, null, default)).Items;
        var sent = (await svc.GetInvitationsAsync(org, camp.Id, "Sent", null, null, null, default)).Items;

        Assert.Equal(used.Id, Assert.Single(joined).Id);
        Assert.Equal(unused.Id, Assert.Single(sent).Id);
    }

    // ══════════════════════════════════════════════════════════════════════════════════════
    // (C) Fallback lịch sử KHÔNG regress
    // ══════════════════════════════════════════════════════════════════════════════════════

    // Membership cũ (invitation_id null) vẫn được ghép bằng email như trước FX1 — migration CỐ Ý không
    // backfill đường-1, nên nếu bỏ fallback thì HR thấy loạt lời mời cũ tụt từ "Joined" về "Sent".
    [Fact]
    public async Task DanhSachLoiMoi_MembershipLichSuKhongCoLink_VanGhepDuocBangEmail()
    {
        using var tdb = new CampaignTestDb();
        var org = Guid.NewGuid();
        var camp = CampaignTestDb.NewCampaign(org, CampaignStatus.Active);
        tdb.Db.Campaigns.Add(camp);
        var inv = SeedInvitation(tdb.Db, camp.Id, "legacy@acme.test");
        tdb.Db.CampaignMemberships.Add(new CampaignMembership
        {
            Id = Guid.NewGuid(),
            CampaignId = camp.Id,
            CandidateId = Guid.NewGuid(),
            InvitationId = null,                  // lịch sử: chưa có quan hệ
            Email = "legacy@acme.test",           // chỉ còn snapshot F5 để bám
            Status = MembershipStatus.Joined,
            JoinedAt = DateTime.UtcNow,
            CreatedAt = DateTime.UtcNow,
            UpdatedAt = DateTime.UtcNow
        });
        await tdb.Db.SaveChangesAsync();

        var rows = (await NewCampaignSvc(tdb.NewContext())
            .GetInvitationsAsync(org, camp.Id, null, null, null, null, default)).Items;

        Assert.Equal(InvitationDeliveryStatus.Joined, Assert.Single(rows).Status);
    }

    // Fallback đường-2 lịch sử (ghép qua cv_submission_id) cũng phải còn.
    [Fact]
    public async Task DanhSachLoiMoi_MembershipLichSuDuong2_VanGhepDuocQuaCvSubmission()
    {
        using var tdb = new CampaignTestDb();
        var org = Guid.NewGuid();
        var camp = CampaignTestDb.NewCampaign(org, CampaignStatus.Active);
        tdb.Db.Campaigns.Add(camp);
        var cv = SeedCv(tdb.Db, camp.Id, "legacy-cv@acme.test", "Phạm C");
        var inv = SeedInvitation(tdb.Db, camp.Id, "legacy-cv@acme.test", campaignCandidateId: cv.Id);
        tdb.Db.CampaignMemberships.Add(new CampaignMembership
        {
            Id = Guid.NewGuid(),
            CampaignId = camp.Id,
            CandidateId = Guid.NewGuid(),
            InvitationId = null,
            CvSubmissionId = cv.Id,
            Email = null,                          // pre-F5: chưa có snapshot email
            Status = MembershipStatus.Joined,
            JoinedAt = DateTime.UtcNow,
            CreatedAt = DateTime.UtcNow,
            UpdatedAt = DateTime.UtcNow
        });
        await tdb.Db.SaveChangesAsync();

        var rows = (await NewCampaignSvc(tdb.NewContext())
            .GetInvitationsAsync(org, camp.Id, null, null, null, null, default)).Items;

        Assert.Equal(InvitationDeliveryStatus.Joined, Assert.Single(rows).Status);
    }

    // ══════════════════════════════════════════════════════════════════════════════════════
    // (D) End-to-end: join THẬT → export CSV cho CẢ hai đường
    //     Giữ cột snapshot F5 (thay vì bỏ đi và join sang invitation) chỉ có nghĩa nếu file HR tải về
    //     vẫn đúng — test này đi qua ĐÚNG đường ghi đã sửa, không seed membership bằng tay.
    // ══════════════════════════════════════════════════════════════════════════════════════
    [Fact]
    public async Task JoinThat_RoiExportCsv_CaHaiDuongDeuCoTenVaEmail()
    {
        using var tdb = new CampaignTestDb();
        var org = Guid.NewGuid();
        var camp = CampaignTestDb.NewCampaign(org, CampaignStatus.Active);
        camp.PassScorePct = 50;
        tdb.Db.Campaigns.Add(camp);

        // Đường-2 (shortlist): tên đến từ cv_submission, email từ lời mời.
        var cv = SeedCv(tdb.Db, camp.Id, "e2e-cv@acme.test", "Nguyễn Văn A, Jr.");
        var inv2 = SeedInvitation(tdb.Db, camp.Id, "e2e-cv@acme.test", campaignCandidateId: cv.Id);
        // Đường-1 (mời thẳng): chỉ có email.
        var inv1 = SeedInvitation(tdb.Db, camp.Id, "e2e-direct@acme.test");
        await tdb.Db.SaveChangesAsync();

        var candidate2 = Guid.Parse("bbbbbbbb-0000-0000-0000-000000000002");
        var candidate1 = Guid.Parse("cccccccc-0000-0000-0000-000000000003");
        await NewParticipation(tdb.NewContext(), candidate2).JoinCampaignAsync(RawTokenOf(inv2), inv2.Email, default);
        await NewParticipation(tdb.NewContext(), candidate1).JoinCampaignAsync(RawTokenOf(inv1), inv1.Email, default);

        // Chấm xong → ranking (nguồn của bảng kết quả E5/E6).
        using (var ctx = tdb.NewContext())
        {
            ctx.CampaignRankings.AddRange(
                new CampaignRanking
                {
                    Id = Guid.NewGuid(), CampaignId = camp.Id, CandidateId = candidate2,
                    SessionId = Guid.NewGuid(), TotalScore = 90m, UpdatedAt = DateTime.UtcNow
                },
                new CampaignRanking
                {
                    Id = Guid.NewGuid(), CampaignId = camp.Id, CandidateId = candidate1,
                    SessionId = Guid.NewGuid(), TotalScore = 80m, UpdatedAt = DateTime.UtcNow.AddMinutes(1)
                });
            await ctx.SaveChangesAsync();
        }

        var result = await NewController(tdb.NewContext(), org).ExportCampaignResults(camp.Id, "csv", default);

        var file = Assert.IsType<FileContentResult>(result);
        var rows = ParseCsv(file.FileContents);
        Assert.Equal(2, rows.Count);

        // Đường-2: có cả tên (từ CV) lẫn email.
        Assert.Equal("Nguyễn Văn A, Jr.", rows[0]["full_name"]);
        Assert.Equal("e2e-cv@acme.test", rows[0]["email"]);
        // Đường-1: không có CV nên không có tên, nhưng email PHẢI có.
        Assert.Equal(string.Empty, rows[1]["full_name"]);
        Assert.Equal("e2e-direct@acme.test", rows[1]["email"]);
    }

    private static List<Dictionary<string, string>> ParseCsv(byte[] bytes)
    {
        using var reader = new StringReader(Encoding.UTF8.GetString(bytes));
        using var csv = new CsvReader(reader, System.Globalization.CultureInfo.InvariantCulture);
        var list = new List<Dictionary<string, string>>();
        csv.Read();
        csv.ReadHeader();
        while (csv.Read())
        {
            var d = new Dictionary<string, string>();
            foreach (var h in csv.HeaderRecord!)
                d[h] = csv.GetField(h) ?? string.Empty;
            list.Add(d);
        }
        return list;
    }
}
