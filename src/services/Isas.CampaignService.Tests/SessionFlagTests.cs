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

    private static void SeedMember(CampaignDbContext db, Guid campaignId, Guid candidateId)
    {
        db.CampaignCandidates.Add(new CampaignCandidate
        {
            Id = Guid.NewGuid(),
            CampaignId = campaignId,
            CandidateId = candidateId,
            ParseStatus = CvParseStatus.Done,
            Status = CandidateStatus.Joined,
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
        SeedMember(tdb.Db, campaign.Id, candidateId);

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
        SeedMember(tdb.Db, campaign.Id, member);   // chỉ `member` là thành viên

        var result = await NewController(tdb.NewContext(), outsider)
            .ReportCandidateFlag(campaign.Id, Guid.NewGuid(),
                new CandidateFlagRequest { SignalType = "tab_switch" }, default);

        Assert.IsType<ForbidResult>(result);
        Assert.Equal(0, FlagCount(tdb, campaign.Id));
    }

    // ── (a) Loại tín hiệu lạ (không thuộc whitelist FE) → 400, KHÔNG ghi row ──
    [Fact]
    public async Task Unknown_signal_type_400()
    {
        using var tdb = new CampaignTestDb();
        var candidateId = Guid.NewGuid();
        var campaign = SeedCampaign(tdb.Db);
        SeedMember(tdb.Db, campaign.Id, candidateId);

        var result = await NewController(tdb.NewContext(), candidateId)
            .ReportCandidateFlag(campaign.Id, Guid.NewGuid(),
                new CandidateFlagRequest { SignalType = "teleport" }, default);

        Assert.IsType<BadRequestObjectResult>(result);
        Assert.Equal(0, FlagCount(tdb, campaign.Id));
    }

    // ── (a) Anti-cheat tắt (+ face-verify tắt) → no-op 204, KHÔNG ghi row (giám sát tắt) ──
    [Fact]
    public async Task Anti_cheat_disabled_no_op_204()
    {
        using var tdb = new CampaignTestDb();
        var candidateId = Guid.NewGuid();
        var campaign = SeedCampaign(tdb.Db, antiCheat: false, faceVerify: false);
        SeedMember(tdb.Db, campaign.Id, candidateId);

        var result = await NewController(tdb.NewContext(), candidateId)
            .ReportCandidateFlag(campaign.Id, Guid.NewGuid(),
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
