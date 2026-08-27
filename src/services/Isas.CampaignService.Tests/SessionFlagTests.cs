using System.Security.Claims;
using Isas.CampaignService.Controllers;
using Isas.CampaignService.DTOs;
using Isas.CampaignService.Models;
using Isas.CampaignService.Services;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Logging;
using Moq;

// 'CampaignService' vừa là namespace vừa là tên class → alias cho rõ ràng.
using CampaignSvc = Isas.CampaignService.Services.CampaignService;

namespace Isas.CampaignService.Tests;

/// <summary>
/// SEC-1 anti-cheat FLAG scaffold — NHẬN + LƯU + PHƠI cờ cho HR (D13/CAMP-12: flag-only, KHÔNG auto-hủy).
/// (a) đường FE (candidate): thành viên → 204 + row; ngoài thành viên → 403; loại lạ → 400; anti-cheat tắt → no-op 204;
/// (b) đường AIService (internal): token đúng → 204 + row; token sai → 401; tín hiệu danh tính lưu khi CHỈ face-verify bật;
/// (c) SEC-4: GetCampaignResults gom cờ theo buổi vào Flags[];
/// (d) toggle: UpdateCampaign FaceVerifyEnabled null → giữ giá trị cũ.
/// Phát hiện gian lận (webcam/tab-switch=FE, face-match=AIService) NGOÀI phạm vi — chỉ hợp đồng ingest.
/// </summary>
public class SessionFlagTests
{
    private const string Token = "internal-secret";

    private static IConfiguration Config() =>
        new ConfigurationBuilder()
            .AddInMemoryCollection(new Dictionary<string, string?> { ["Internal:Token"] = Token })
            .Build();

    private static SessionFlagController NewController(CampaignDbContext db, Guid? candidateId = null)
    {
        var controller = new SessionFlagController(
            db, Config(), Mock.Of<ILogger<SessionFlagController>>());

        var claims = new List<Claim>();
        if (candidateId is not null)
            claims.Add(new Claim(ClaimTypes.NameIdentifier, candidateId.Value.ToString()));

        controller.ControllerContext = new ControllerContext
        {
            HttpContext = new DefaultHttpContext
            {
                User = new ClaimsPrincipal(new ClaimsIdentity(claims, "Test"))
            }
        };
        return controller;
    }

    private static Campaign SeedCampaign(
        CampaignDbContext db, bool antiCheat = true, bool faceVerify = false)
    {
        var c = CampaignTestDb.NewCampaign(Guid.NewGuid(), CampaignStatus.Active, antiCheat);
        c.FaceVerifyEnabled = faceVerify;
        db.Campaigns.Add(c);
        db.SaveChanges();
        return c;
    }

    // Q4 — `sessionId` = buổi thi ĐÃ Start của thành viên này (ParticipationService ghi lúc Start).
    // Trước Q4 helper này để SessionId null còn test truyền Guid.NewGuid() vào route, tức mọi test đều
    // đi qua đúng ca "cờ gửi vào buổi KHÔNG phải của mình" mà vẫn kỳ vọng 204 ⇒ bộ test cũ khoá đúng
    // hành vi sai. Nay seed và route dùng CHUNG một sessionId.
    private static void SeedMember(
        CampaignDbContext db, Guid campaignId, Guid candidateId, Guid? sessionId = null)
    {
        // DB16 — membership (ownership check flags) sống ở campaign_membership.
        db.CampaignMemberships.Add(new CampaignMembership
        {
            Id = Guid.NewGuid(),
            CampaignId = campaignId,
            CandidateId = candidateId,
            SessionId = sessionId,
            Status = MembershipStatus.Joined,
            JoinedAt = DateTime.UtcNow,
            CreatedAt = DateTime.UtcNow,
            UpdatedAt = DateTime.UtcNow
        });
        db.SaveChanges();
    }

    private static int FlagCount(CampaignTestDb tdb, Guid campaignId)
    {
        using var check = tdb.NewContext();
        return check.SessionFlags.Count(f => f.CampaignId == campaignId);
    }

    // ── (a) Đường FE — thành viên campaign phát cờ hợp lệ → 204 + row đúng dữ liệu ──
    [Fact]
    public async Task Candidate_member_ghi_flag_204_va_row()
    {
        using var tdb = new CampaignTestDb();
        var candidateId = Guid.NewGuid();
        var sessionId = Guid.NewGuid();
        var campaign = SeedCampaign(tdb.Db, antiCheat: true);
        SeedMember(tdb.Db, campaign.Id, candidateId, sessionId);

        var result = await NewController(tdb.NewContext(), candidateId)
            .ReportCandidateFlag(campaign.Id, sessionId,
                new CandidateFlagRequest { SignalType = "tab_switch", Note = "chuyển tab" }, default);

        Assert.IsType<NoContentResult>(result);

        using var check = tdb.NewContext();
        var flag = Assert.Single(check.SessionFlags.Where(f => f.CampaignId == campaign.Id));
        Assert.Equal(sessionId, flag.SessionId);
        Assert.Equal(candidateId, flag.CandidateId);
        Assert.Equal("tab_switch", flag.SignalType);
        Assert.Equal("chuyển tab", flag.Note);
    }

    // ── (a) Ngoài thành viên campaign → 403, KHÔNG ghi row ──
    [Fact]
    public async Task Non_member_candidate_403()
    {
        using var tdb = new CampaignTestDb();
        var member = Guid.NewGuid();
        var outsider = Guid.NewGuid();
        var campaign = SeedCampaign(tdb.Db);
        SeedMember(tdb.Db, campaign.Id, member, Guid.NewGuid());   // chỉ `member` là thành viên

        var result = await NewController(tdb.NewContext(), outsider)
            .ReportCandidateFlag(campaign.Id, Guid.NewGuid(),
                new CandidateFlagRequest { SignalType = "tab_switch" }, default);

        Assert.IsType<ForbidResult>(result);
        Assert.Equal(0, FlagCount(tdb, campaign.Id));
    }

    // ── 🔴 Q4 — THÀNH VIÊN campaign cắm cờ vào buổi thi của THÀNH VIÊN KHÁC → 403, KHÔNG ghi row ──
    // Đã xảy ra trên prod (1 buổi mang cờ do 2 candidate khác nhau gửi): guard cũ chỉ hỏi "có phải
    // thành viên campaign không", còn sessionId lấy thẳng từ route. Vì `unscoredFlagged` (R7) xếp theo
    // TỔNG số cờ mỗi buổi, đây là đường bôi bẩn ứng viên khác trong bảng "đáng ngờ" của HR.
    [Fact]
    public async Task Q4_ThanhVien_CamCoVaoBuoiCuaNguoiKhac_403()
    {
        using var tdb = new CampaignTestDb();
        var attacker = Guid.NewGuid();
        var victim = Guid.NewGuid();
        var attackerSession = Guid.NewGuid();
        var victimSession = Guid.NewGuid();
        var campaign = SeedCampaign(tdb.Db, antiCheat: true);
        SeedMember(tdb.Db, campaign.Id, attacker, attackerSession);   // CẢ HAI đều là thành viên hợp lệ
        SeedMember(tdb.Db, campaign.Id, victim, victimSession);

        var result = await NewController(tdb.NewContext(), attacker)
            .ReportCandidateFlag(campaign.Id, victimSession,
                new CandidateFlagRequest { SignalType = "tab_switch" }, default);

        Assert.IsType<ForbidResult>(result);
        Assert.Equal(0, FlagCount(tdb, campaign.Id));   // buổi của nạn nhân KHÔNG dính cờ nào
    }

    // Q4 — cùng ranh giới, phía còn lại: buổi CỦA CHÍNH MÌNH vẫn ghi được (guard không chặn quá tay).
    [Fact]
    public async Task Q4_ThanhVien_CamCoVaoBuoiCuaChinhMinh_204()
    {
        using var tdb = new CampaignTestDb();
        var me = Guid.NewGuid();
        var other = Guid.NewGuid();
        var mySession = Guid.NewGuid();
        var campaign = SeedCampaign(tdb.Db, antiCheat: true);
        SeedMember(tdb.Db, campaign.Id, me, mySession);
        SeedMember(tdb.Db, campaign.Id, other, Guid.NewGuid());

        var result = await NewController(tdb.NewContext(), me)
            .ReportCandidateFlag(campaign.Id, mySession,
                new CandidateFlagRequest { SignalType = "focus_lost" }, default);

        Assert.IsType<NoContentResult>(result);
        using var check = tdb.NewContext();
        var flag = Assert.Single(check.SessionFlags.Where(f => f.CampaignId == campaign.Id));
        Assert.Equal(mySession, flag.SessionId);
        Assert.Equal(me, flag.CandidateId);
    }

    // ── (a) Loại tín hiệu lạ (không thuộc whitelist FE) → 400, KHÔNG ghi row ──
    [Fact]
    public async Task Unknown_signal_type_400()
    {
        using var tdb = new CampaignTestDb();
        var candidateId = Guid.NewGuid();
        var sessionId = Guid.NewGuid();
        var campaign = SeedCampaign(tdb.Db);
        SeedMember(tdb.Db, campaign.Id, candidateId, sessionId);

        var result = await NewController(tdb.NewContext(), candidateId)
            .ReportCandidateFlag(campaign.Id, sessionId,
                new CandidateFlagRequest { SignalType = "teleport" }, default);

        Assert.IsType<BadRequestObjectResult>(result);
        Assert.Equal(0, FlagCount(tdb, campaign.Id));
    }

    // ── F4 — `camera_blocked`: OS/trình duyệt từ chối camera ────────────────────────
    // Trước F4, FE nuốt lỗi này ⇒ ứng viên thi tiếp không bị giám sát mặt mà HR không có cờ nào.
    [Fact]
    public async Task Camera_blocked_duoc_chap_nhan_va_ghi_row()
    {
        using var tdb = new CampaignTestDb();
        var candidateId = Guid.NewGuid();
        var sessionId = Guid.NewGuid();
        var campaign = SeedCampaign(tdb.Db, antiCheat: true);
        SeedMember(tdb.Db, campaign.Id, candidateId, sessionId);

        var result = await NewController(tdb.NewContext(), candidateId)
            .ReportCandidateFlag(campaign.Id, sessionId,
                new CandidateFlagRequest { SignalType = "camera_blocked", Note = "NotAllowedError" }, default);

        Assert.IsType<NoContentResult>(result);

        using var check = tdb.NewContext();
        var flag = Assert.Single(check.SessionFlags.Where(f => f.CampaignId == campaign.Id));
        Assert.Equal("camera_blocked", flag.SignalType);
        Assert.Equal(sessionId, flag.SessionId);
        Assert.Equal(candidateId, flag.CandidateId);
        Assert.Equal("NotAllowedError", flag.Note);
    }

    // 🔴 `camera_blocked` là cờ MÔI TRƯỜNG, KHÔNG phải tín hiệu DANH TÍNH.
    // Nếu ai đó thêm nhầm nó vào IdentitySignals thì điều kiện lưu đổi: campaign chỉ bật
    // face_verify (anti-cheat TẮT) sẽ bắt đầu lưu cờ này. Test khoá đúng ranh giới đó.
    [Fact]
    public async Task Camera_blocked_khong_phai_tin_hieu_danh_tinh()
    {
        using var tdb = new CampaignTestDb();
        var candidateId = Guid.NewGuid();
        var sessionId = Guid.NewGuid();
        // anti-cheat TẮT, face-verify BẬT → chỉ tín hiệu danh tính mới được lưu.
        var campaign = SeedCampaign(tdb.Db, antiCheat: false, faceVerify: true);
        SeedMember(tdb.Db, campaign.Id, candidateId, sessionId);

        var result = await NewController(tdb.NewContext(), candidateId)
            .ReportCandidateFlag(campaign.Id, sessionId,
                new CandidateFlagRequest { SignalType = "camera_blocked" }, default);

        Assert.IsType<NoContentResult>(result);          // vẫn 204 (no-op idempotent)
        Assert.Equal(0, FlagCount(tdb, campaign.Id));    // nhưng KHÔNG lưu
    }

    // ── AC1 — `monitoring_gap`: nhịp giám sát 30s bị đứt (tab ngủ/máy sleep/mạng rớt) ────────────
    // Không có ảnh nào để so trong khoảng đó ⇒ HR phải thấy "khoảng mù", nếu không thì buổi bị đứt
    // giám sát trông y hệt buổi sạch. Whitelist FE phải nhận loại này, else 400 và cờ rơi mất.
    [Fact]
    public async Task AC1_Monitoring_gap_duoc_chap_nhan_va_ghi_row()
    {
        using var tdb = new CampaignTestDb();
        var candidateId = Guid.NewGuid();
        var sessionId = Guid.NewGuid();
        var campaign = SeedCampaign(tdb.Db, antiCheat: true);
        SeedMember(tdb.Db, campaign.Id, candidateId, sessionId);

        var result = await NewController(tdb.NewContext(), candidateId)
            .ReportCandidateFlag(campaign.Id, sessionId,
                new CandidateFlagRequest { SignalType = "monitoring_gap", Note = "gián đoạn 94s" }, default);

        Assert.IsType<NoContentResult>(result);

        using var check = tdb.NewContext();
        var flag = Assert.Single(check.SessionFlags.Where(f => f.CampaignId == campaign.Id));
        Assert.Equal("monitoring_gap", flag.SignalType);
        Assert.Equal(sessionId, flag.SessionId);
        Assert.Equal(candidateId, flag.CandidateId);
        Assert.Equal("gián đoạn 94s", flag.Note);
    }

    // 🔴 AC1 — `monitoring_gap` là cờ MÔI TRƯỜNG, KHÔNG phải tín hiệu DANH TÍNH (cùng lập luận F4).
    // Thêm nhầm vào IdentitySignals ⇒ đổi điều kiện lưu: campaign chỉ bật face_verify (anti-cheat TẮT)
    // sẽ bắt đầu ghi cờ này. Nó nói "không quan sát được", KHÔNG nói "sai người".
    [Fact]
    public async Task AC1_Monitoring_gap_khong_phai_tin_hieu_danh_tinh()
    {
        using var tdb = new CampaignTestDb();
        var candidateId = Guid.NewGuid();
        var sessionId = Guid.NewGuid();
        // anti-cheat TẮT, face-verify BẬT → chỉ tín hiệu danh tính mới được lưu.
        var campaign = SeedCampaign(tdb.Db, antiCheat: false, faceVerify: true);
        SeedMember(tdb.Db, campaign.Id, candidateId, sessionId);

        var result = await NewController(tdb.NewContext(), candidateId)
            .ReportCandidateFlag(campaign.Id, sessionId,
                new CandidateFlagRequest { SignalType = "monitoring_gap" }, default);

        Assert.IsType<NoContentResult>(result);          // vẫn 204 (no-op idempotent)
        Assert.Equal(0, FlagCount(tdb, campaign.Id));    // nhưng KHÔNG lưu
    }

    // AC1 — ẩn danh (không claim NameIdentifier) → 401, KHÔNG ghi row. [Authorize] chặn ở tầng
    // pipeline, guard này là lớp thứ hai (controller cũng chạy được khi test gọi thẳng).
    [Fact]
    public async Task AC1_Monitoring_gap_an_danh_401()
    {
        using var tdb = new CampaignTestDb();
        var sessionId = Guid.NewGuid();
        var campaign = SeedCampaign(tdb.Db, antiCheat: true);

        var result = await NewController(tdb.NewContext(), candidateId: null)
            .ReportCandidateFlag(campaign.Id, sessionId,
                new CandidateFlagRequest { SignalType = "monitoring_gap" }, default);

        Assert.IsType<UnauthorizedResult>(result);
        Assert.Equal(0, FlagCount(tdb, campaign.Id));
    }

    // AC1 — thành viên campaign cắm `monitoring_gap` vào buổi NGƯỜI KHÁC → 403 (Q4 áp cho cả loại mới).
    [Fact]
    public async Task AC1_Monitoring_gap_buoi_nguoi_khac_403()
    {
        using var tdb = new CampaignTestDb();
        var attacker = Guid.NewGuid();
        var victim = Guid.NewGuid();
        var victimSession = Guid.NewGuid();
        var campaign = SeedCampaign(tdb.Db, antiCheat: true);
        SeedMember(tdb.Db, campaign.Id, attacker, Guid.NewGuid());
        SeedMember(tdb.Db, campaign.Id, victim, victimSession);

        var result = await NewController(tdb.NewContext(), attacker)
            .ReportCandidateFlag(campaign.Id, victimSession,
                new CandidateFlagRequest { SignalType = "monitoring_gap" }, default);

        Assert.IsType<ForbidResult>(result);
        Assert.Equal(0, FlagCount(tdb, campaign.Id));
    }

    // ── (a) Anti-cheat tắt (+ face-verify tắt) → no-op 204, KHÔNG ghi row (giám sát tắt) ──
    [Fact]
    public async Task Anti_cheat_disabled_no_op_204()
    {
        using var tdb = new CampaignTestDb();
        var candidateId = Guid.NewGuid();
        var sessionId = Guid.NewGuid();
        var campaign = SeedCampaign(tdb.Db, antiCheat: false, faceVerify: false);
        SeedMember(tdb.Db, campaign.Id, candidateId, sessionId);

        var result = await NewController(tdb.NewContext(), candidateId)
            .ReportCandidateFlag(campaign.Id, sessionId,
                new CandidateFlagRequest { SignalType = "paste" }, default);

        Assert.IsType<NoContentResult>(result);
        Assert.Equal(0, FlagCount(tdb, campaign.Id));   // toggle off → không lưu
    }

    // ── (b) Đường AIService — token đúng + tín hiệu AI hợp lệ → 204 + row ──
    [Fact]
    public async Task Internal_valid_token_ghi_flag_204()
    {
        using var tdb = new CampaignTestDb();
        var campaign = SeedCampaign(tdb.Db, antiCheat: true);
        var sessionId = Guid.NewGuid();
        var candidateId = Guid.NewGuid();

        var result = await NewController(tdb.NewContext()).ReportInternalFlag(
            new InternalFlagRequest
            {
                SessionId = sessionId,
                CampaignId = campaign.Id,
                CandidateId = candidateId,
                SignalType = "multiple_faces",
                Note = "2 khuôn mặt"
            },
            Token, default);

        Assert.IsType<NoContentResult>(result);
        using var check = tdb.NewContext();
        var flag = Assert.Single(check.SessionFlags.Where(f => f.CampaignId == campaign.Id));
        Assert.Equal("multiple_faces", flag.SignalType);
        Assert.Equal(sessionId, flag.SessionId);
    }

    // ── (b) Đường AIService — token sai → 401, KHÔNG ghi row ──
    [Fact]
    public async Task Internal_wrong_token_401()
    {
        using var tdb = new CampaignTestDb();
        var campaign = SeedCampaign(tdb.Db, antiCheat: true);

        var result = await NewController(tdb.NewContext()).ReportInternalFlag(
            new InternalFlagRequest
            {
                SessionId = Guid.NewGuid(),
                CampaignId = campaign.Id,
                CandidateId = Guid.NewGuid(),
                SignalType = "face_mismatch"
            },
            "wrong-token", default);

        Assert.IsType<UnauthorizedObjectResult>(result);
        Assert.Equal(0, FlagCount(tdb, campaign.Id));
    }

    // ── (b) Token compare NAY hằng-thời-gian (FixedTimeEquals) — hành vi reject giữ nguyên, chỉ đổi
    // cơ chế so sánh. Token SAI KHÁC ĐỘ DÀI với token thật (path length-mismatch của FixedTimeEquals
    // trả false, KHÔNG ném exception) vẫn 401, không ghi row.
    [Fact]
    public async Task Internal_token_khac_do_dai_van_401_khong_nem_loi()
    {
        using var tdb = new CampaignTestDb();
        var campaign = SeedCampaign(tdb.Db, antiCheat: true);

        var result = await NewController(tdb.NewContext()).ReportInternalFlag(
            new InternalFlagRequest
            {
                SessionId = Guid.NewGuid(),
                CampaignId = campaign.Id,
                CandidateId = Guid.NewGuid(),
                SignalType = "no_face"
            },
            "ngan-hon-nhieu", default);   // ngắn hơn Token ("internal-secret") — khác độ dài

        Assert.IsType<UnauthorizedObjectResult>(result);
        Assert.Equal(0, FlagCount(tdb, campaign.Id));
    }

    // ── (b) Token CÙNG ĐỘ DÀI nhưng khác nội dung (path so-khớp-hằng-thời-gian thật sự chạy tới cuối) → 401.
    [Fact]
    public async Task Internal_token_cung_do_dai_khac_noi_dung_van_401()
    {
        using var tdb = new CampaignTestDb();
        var campaign = SeedCampaign(tdb.Db, antiCheat: true);
        Assert.Equal(Token.Length, "internal-secreX".Length);   // giữ đúng "cùng độ dài" nếu Token đổi

        var result = await NewController(tdb.NewContext()).ReportInternalFlag(
            new InternalFlagRequest
            {
                SessionId = Guid.NewGuid(),
                CampaignId = campaign.Id,
                CandidateId = Guid.NewGuid(),
                SignalType = "no_face"
            },
            "internal-secreX", default);   // đổi ký tự cuối, cùng độ dài với Token

        Assert.IsType<UnauthorizedObjectResult>(result);
        Assert.Equal(0, FlagCount(tdb, campaign.Id));
    }

    // ── (b) Tín hiệu DANH TÍNH lưu khi CHỈ face-verify bật (anti-cheat tắt); tín hiệu thường thì no-op ──
    [Fact]
    public async Task Identity_signal_persisted_when_only_faceverify_on()
    {
        using var tdb = new CampaignTestDb();
        var campaign = SeedCampaign(tdb.Db, antiCheat: false, faceVerify: true);

        // identity signal (face_mismatch) → lưu vì face_verify_enabled bật
        var idResult = await NewController(tdb.NewContext()).ReportInternalFlag(
            new InternalFlagRequest
            {
                SessionId = Guid.NewGuid(), CampaignId = campaign.Id,
                CandidateId = Guid.NewGuid(), SignalType = "identity_unverified"
            }, Token, default);
        Assert.IsType<NoContentResult>(idResult);

        // tín hiệu thường (multi_voice) → no-op vì anti-cheat tắt & không phải danh tính
        var voiceResult = await NewController(tdb.NewContext()).ReportInternalFlag(
            new InternalFlagRequest
            {
                SessionId = Guid.NewGuid(), CampaignId = campaign.Id,
                CandidateId = Guid.NewGuid(), SignalType = "multi_voice"
            }, Token, default);
        Assert.IsType<NoContentResult>(voiceResult);

        using var check = tdb.NewContext();
        var flags = check.SessionFlags.Where(f => f.CampaignId == campaign.Id).ToList();
        Assert.Single(flags);
        Assert.Equal("identity_unverified", flags[0].SignalType);
    }

    // ── (c) SEC-4 — GetCampaignResults gom cờ theo buổi vào Flags[] (signal_type → count) ──
    [Fact]
    public async Task GetCampaignResults_returns_Flags()
    {
        using var tdb = new CampaignTestDb();
        var orgId = Guid.NewGuid();
        var campaign = CampaignTestDb.NewCampaign(orgId, CampaignStatus.Active);
        tdb.Db.Campaigns.Add(campaign);
        tdb.Db.SaveChanges();

        var sessionId = Guid.NewGuid();
        tdb.Db.CampaignRankings.Add(new CampaignRanking
        {
            Id = Guid.NewGuid(), CampaignId = campaign.Id, CandidateId = Guid.NewGuid(),
            SessionId = sessionId, TotalScore = 80.00m, UpdatedAt = DateTime.UtcNow
        });
        // 2× tab_switch + 1× no_face cho cùng buổi → gom {tab_switch:2, no_face:1}.
        void AddFlag(string type, string? note) => tdb.Db.SessionFlags.Add(new SessionFlag
        {
            Id = Guid.NewGuid(), SessionId = sessionId, CampaignId = campaign.Id,
            CandidateId = Guid.NewGuid(), SignalType = type, Note = note, DetectedAt = DateTime.UtcNow
        });
        AddFlag("tab_switch", null);
        AddFlag("tab_switch", null);
        AddFlag("no_face", "không thấy mặt");
        tdb.Db.SaveChanges();

        var svc = new CampaignSvc(tdb.NewContext(), Mock.Of<IFileService>(), Mock.Of<ILogger<CampaignSvc>>(),
            Mock.Of<IParserService>(), Mock.Of<ICriteriaSuggester>(), Mock.Of<IInvitationEmailPublisher>());
        var res = await svc.GetCampaignResultsAsync(orgId, campaign.Id, default);

        var row = Assert.Single(res.Results);
        Assert.Equal(2, row.Flags.Count);
        Assert.Equal(2, row.Flags.Single(f => f.Type == "tab_switch").Count);
        var noFace = row.Flags.Single(f => f.Type == "no_face");
        Assert.Equal(1, noFace.Count);
        Assert.Equal("không thấy mặt", noFace.Note);
    }

    // ── (c') Buổi không có cờ → Flags rỗng (non-breaking default) ──
    [Fact]
    public async Task GetCampaignResults_no_flags_empty_list()
    {
        using var tdb = new CampaignTestDb();
        var orgId = Guid.NewGuid();
        var campaign = CampaignTestDb.NewCampaign(orgId, CampaignStatus.Active);
        tdb.Db.Campaigns.Add(campaign);
        tdb.Db.CampaignRankings.Add(new CampaignRanking
        {
            Id = Guid.NewGuid(), CampaignId = campaign.Id, CandidateId = Guid.NewGuid(),
            SessionId = Guid.NewGuid(), TotalScore = 50.00m, UpdatedAt = DateTime.UtcNow
        });
        tdb.Db.SaveChanges();

        var svc = new CampaignSvc(tdb.NewContext(), Mock.Of<IFileService>(), Mock.Of<ILogger<CampaignSvc>>(),
            Mock.Of<IParserService>(), Mock.Of<ICriteriaSuggester>(), Mock.Of<IInvitationEmailPublisher>());
        var res = await svc.GetCampaignResultsAsync(orgId, campaign.Id, default);

        Assert.Empty(Assert.Single(res.Results).Flags);
    }

    // ── (d) Toggle — UpdateCampaign FaceVerifyEnabled null → GIỮ giá trị cũ (như AntiCheatEnabled C3) ──
    [Fact]
    public async Task Update_FaceVerify_null_giu_gia_tri_cu()
    {
        using var tdb = new CampaignTestDb();
        var owner = Guid.NewGuid();
        var camp = CampaignTestDb.NewCampaign(owner);
        camp.FaceVerifyEnabled = true;
        tdb.Db.Campaigns.Add(camp);
        tdb.Db.SaveChanges();

        var svc = new CampaignSvc(tdb.NewContext(), Mock.Of<IFileService>(), Mock.Of<ILogger<CampaignSvc>>(),
            Mock.Of<IParserService>(), Mock.Of<ICriteriaSuggester>(), Mock.Of<IInvitationEmailPublisher>());
        var res = await svc.UpdateCampaignAsync(owner, owner, camp.Id,
            new UpdateCampaignRequest { Title = "New", FaceVerifyEnabled = null }, default);

        Assert.True(res.FaceVerifyEnabled);   // vẫn true (null = không đổi)

        // Set tường minh false → cập nhật.
        var res2 = await svc.UpdateCampaignAsync(owner, owner, camp.Id,
            new UpdateCampaignRequest { Title = "New", FaceVerifyEnabled = false }, default);
        Assert.False(res2.FaceVerifyEnabled);
    }
}
