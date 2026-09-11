using System.Security.Claims;
using Isas.CampaignService.Controllers;
using Isas.CampaignService.DTOs;
using Isas.CampaignService.Models;
using Isas.CampaignService.Services;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Diagnostics;
using Microsoft.Extensions.Logging;
using Moq;

using CampaignSvc = Isas.CampaignService.Services.CampaignService;

namespace Isas.CampaignService.Tests;

/// <summary>
/// E11c — LỊCH SỬ điều chỉnh của HR (`ranking_overrides`, append-only) + đường đọc cho HR.
/// Trước E11c: 5 cột override_* trên ranking chỉ giữ lần mới nhất, huỷ = null hết ⇒ HR mất "ai từng sửa, vì sao";
/// trail chỉ nằm trong audit_logs (chuỗi tự do, không endpoint). Nay MỖI lần Set/Clear = 1 dòng history ghi CÙNG
/// SaveChanges với ranking + audit; GET override-history trả mới-nhất-trước, gate y hệt override (org + ranking row).
/// </summary>
public class ResultOverrideHistoryE11cTests
{
    private static CampaignSvc NewService(CampaignDbContext db, ICampaignSessionClient? client = null) =>
        new(db, Mock.Of<IFileService>(), Mock.Of<ILogger<CampaignSvc>>(),
            Mock.Of<IParserService>(), Mock.Of<ICriteriaSuggester>(),
            Mock.Of<IInvitationEmailPublisher>(), client);

    private static Campaign SeedCampaign(CampaignDbContext db, Guid orgId)
    {
        var c = CampaignTestDb.NewCampaign(orgId, CampaignStatus.Active);
        c.PassScorePct = 50;
        db.Campaigns.Add(c);
        db.SaveChanges();
        return c;
    }

    private static CampaignRanking SeedRanking(CampaignDbContext db, Guid campaignId, decimal score)
    {
        var r = new CampaignRanking
        {
            Id = Guid.NewGuid(), CampaignId = campaignId, CandidateId = Guid.NewGuid(),
            SessionId = Guid.NewGuid(), TotalScore = score, UpdatedAt = DateTime.UtcNow
        };
        db.CampaignRankings.Add(r);
        db.SaveChanges();
        return r;
    }

    private static OverrideResultRequest Set(decimal? score, string? result, string note) =>
        new() { Score = score, Result = result, Note = note };

    private static OverrideResultRequest Clear(string note) => new() { Score = null, Result = null, Note = note };

    // ── ghi ──────────────────────────────────────────────────────────────────

    [Fact]
    public async Task Set_GhiMotDongHistory_DuField_CungMocVoiRanking_AuditVanMotDong()
    {
        using var tdb = new CampaignTestDb();
        var orgId = Guid.NewGuid();
        var actor = Guid.NewGuid();
        var campaign = SeedCampaign(tdb.Db, orgId);
        var ranking = SeedRanking(tdb.Db, campaign.Id, 35.00m);

        await NewService(tdb.NewContext()).OverrideResultAsync(
            orgId, actor, "  hr@isas.local ", campaign.Id, ranking.SessionId, Set(72m, "Pass", "  Lý do A  "), default);

        using var db = tdb.NewContext();
        var row = Assert.Single(await db.RankingOverrides.ToListAsync());
        Assert.Equal(ranking.Id, row.RankingId);
        Assert.Equal(campaign.Id, row.CampaignId);
        Assert.Equal(ranking.SessionId, row.SessionId);
        Assert.Equal("Set", row.Kind);
        Assert.Equal(72m, row.Score);
        Assert.Equal("Pass", row.Result);
        Assert.Equal("Lý do A", row.Note);           // trim — khớp ranking.OverrideNote
        Assert.Equal(actor, row.ActorUserId);
        Assert.Equal("hr@isas.local", row.ActorEmail); // snapshot claim, trim
        Assert.Equal("Live", row.Source);

        var after = await db.CampaignRankings.SingleAsync(r => r.Id == ranking.Id);
        Assert.Equal(after.OverriddenAt, row.CreatedAt);   // MỘT mốc `now` cho cả hai
        Assert.Equal(35.00m, after.TotalScore);            // điểm AI gốc không đổi
        Assert.Equal(1, await db.AuditLogs.CountAsync(a => a.Action == AuditAction.OverrideResult));
    }

    [Fact]
    public async Task SetSetClear_BaDong_MoiNhatTruoc_ClearGiuNote_RankingVeNull()
    {
        using var tdb = new CampaignTestDb();
        var orgId = Guid.NewGuid();
        var actor = Guid.NewGuid();
        var campaign = SeedCampaign(tdb.Db, orgId);
        var ranking = SeedRanking(tdb.Db, campaign.Id, 35.00m);

        var svc = NewService(tdb.NewContext());
        await svc.OverrideResultAsync(orgId, actor, "hr@isas.local", campaign.Id, ranking.SessionId, Set(72m, "Pass", "A"), default);
        await Task.Delay(5);
        await svc.OverrideResultAsync(orgId, actor, "hr@isas.local", campaign.Id, ranking.SessionId, Set(60m, "Fail", "B"), default);
        await Task.Delay(5);
        await svc.OverrideResultAsync(orgId, actor, "hr@isas.local", campaign.Id, ranking.SessionId, Clear("về AI"), default);

        var history = await NewService(tdb.NewContext()).GetOverrideHistoryAsync(orgId, campaign.Id, ranking.SessionId, default);

        Assert.Equal(ranking.SessionId, history.SessionId);
        Assert.Equal(3, history.Items.Count);
        Assert.Equal(new[] { "Clear", "Set", "Set" }, history.Items.Select(i => i.Kind));
        Assert.Equal(new[] { "về AI", "B", "A" }, history.Items.Select(i => i.Note));   // mới-nhất-trước
        Assert.True(history.Items[0].At >= history.Items[1].At && history.Items[1].At >= history.Items[2].At);

        var clear = history.Items[0];
        Assert.Null(clear.Score);
        Assert.Null(clear.Result);
        Assert.Equal("hr@isas.local", clear.ActorEmail);
        Assert.Equal("Live", clear.Source);

        // Ranking = trạng thái HIỆN TẠI (huỷ ⇒ null hết) — lịch sử vẫn giữ trọn 3 lần.
        var after = await tdb.NewContext().CampaignRankings.SingleAsync(r => r.Id == ranking.Id);
        Assert.Null(after.OverrideScore);
        Assert.Null(after.OverrideNote);
        Assert.Null(after.OverriddenAt);
        Assert.Equal(35.00m, after.TotalScore);
    }

    private sealed class SaveCounter : SaveChangesInterceptor
    {
        public int Saves;
        public override InterceptionResult<int> SavingChanges(DbContextEventData eventData, InterceptionResult<int> result)
        { Saves++; return result; }
        public override ValueTask<InterceptionResult<int>> SavingChangesAsync(DbContextEventData eventData, InterceptionResult<int> result, CancellationToken ct = default)
        { Saves++; return new ValueTask<InterceptionResult<int>>(result); }
    }

    [Fact]
    public async Task HistoryRankingAudit_CungMotSaveChanges()
    {
        using var tdb = new CampaignTestDb();
        var orgId = Guid.NewGuid();
        var campaign = SeedCampaign(tdb.Db, orgId);
        var ranking = SeedRanking(tdb.Db, campaign.Id, 35.00m);
        var counter = new SaveCounter();

        await NewService(tdb.NewContext(counter)).OverrideResultAsync(
            orgId, Guid.NewGuid(), "hr@isas.local", campaign.Id, ranking.SessionId, Set(72m, "Pass", "A"), default);

        Assert.Equal(1, counter.Saves);   // một transaction ngầm cho ranking + history + audit
        using var db = tdb.NewContext();
        Assert.Equal(1, await db.RankingOverrides.CountAsync());
        Assert.Equal(1, await db.AuditLogs.CountAsync(a => a.Action == AuditAction.OverrideResult));
        Assert.Equal(72m, (await db.CampaignRankings.SingleAsync()).OverrideScore);
    }

    [Fact]
    public async Task ThieuEmail_ActorEmailNull_KhongNem()
    {
        using var tdb = new CampaignTestDb();
        var orgId = Guid.NewGuid();
        var campaign = SeedCampaign(tdb.Db, orgId);
        var ranking = SeedRanking(tdb.Db, campaign.Id, 35.00m);

        await NewService(tdb.NewContext()).OverrideResultAsync(
            orgId, Guid.NewGuid(), "   ", campaign.Id, ranking.SessionId, Set(72m, "Pass", "A"), default);

        Assert.Null((await tdb.NewContext().RankingOverrides.SingleAsync()).ActorEmail);
    }

    [Fact]
    public async Task RequestKhongHopLe_KhongGhiHistory()
    {
        using var tdb = new CampaignTestDb();
        var orgId = Guid.NewGuid();
        var campaign = SeedCampaign(tdb.Db, orgId);
        var ranking = SeedRanking(tdb.Db, campaign.Id, 35.00m);

        await Assert.ThrowsAsync<ArgumentException>(() => NewService(tdb.NewContext()).OverrideResultAsync(
            orgId, Guid.NewGuid(), "hr@isas.local", campaign.Id, ranking.SessionId, Set(72m, "Maybe", "A"), default));

        Assert.Equal(0, await tdb.NewContext().RankingOverrides.CountAsync());
    }

    // ── đọc ──────────────────────────────────────────────────────────────────

    [Fact]
    public async Task GetHistory_ChuaCoLanNao_ItemsRong()
    {
        using var tdb = new CampaignTestDb();
        var orgId = Guid.NewGuid();
        var campaign = SeedCampaign(tdb.Db, orgId);
        var ranking = SeedRanking(tdb.Db, campaign.Id, 35.00m);

        var history = await NewService(tdb.NewContext()).GetOverrideHistoryAsync(orgId, campaign.Id, ranking.SessionId, default);
        Assert.Empty(history.Items);
    }

    [Fact]
    public async Task GetHistory_OrgKhac_404()
    {
        using var tdb = new CampaignTestDb();
        var orgId = Guid.NewGuid();
        var campaign = SeedCampaign(tdb.Db, orgId);
        var ranking = SeedRanking(tdb.Db, campaign.Id, 35.00m);
        await NewService(tdb.NewContext()).OverrideResultAsync(
            orgId, Guid.NewGuid(), "hr@isas.local", campaign.Id, ranking.SessionId, Set(72m, "Pass", "A"), default);

        await Assert.ThrowsAsync<KeyNotFoundException>(() =>
            NewService(tdb.NewContext()).GetOverrideHistoryAsync(Guid.NewGuid(), campaign.Id, ranking.SessionId, default));
    }

    [Fact]
    public async Task GetHistory_SessionChuaCham_404()
    {
        using var tdb = new CampaignTestDb();
        var orgId = Guid.NewGuid();
        var campaign = SeedCampaign(tdb.Db, orgId);

        await Assert.ThrowsAsync<KeyNotFoundException>(() =>
            NewService(tdb.NewContext()).GetOverrideHistoryAsync(orgId, campaign.Id, Guid.NewGuid(), default));
    }

    [Fact]
    public async Task GetHistory_ChiLichSuCuaDungRanking()
    {
        using var tdb = new CampaignTestDb();
        var orgId = Guid.NewGuid();
        var campaign = SeedCampaign(tdb.Db, orgId);
        var a = SeedRanking(tdb.Db, campaign.Id, 35.00m);
        var b = SeedRanking(tdb.Db, campaign.Id, 40.00m);
        var svc = NewService(tdb.NewContext());
        await svc.OverrideResultAsync(orgId, Guid.NewGuid(), "hr@isas.local", campaign.Id, a.SessionId, Set(72m, "Pass", "cho A"), default);
        await svc.OverrideResultAsync(orgId, Guid.NewGuid(), "hr@isas.local", campaign.Id, b.SessionId, Set(10m, "Fail", "cho B"), default);

        var history = await NewService(tdb.NewContext()).GetOverrideHistoryAsync(orgId, campaign.Id, b.SessionId, default);
        Assert.Equal("cho B", Assert.Single(history.Items).Note);
    }

    [Fact]
    public async Task CampaignSoftDelete_HistoryBiQueryFilterAn()
    {
        using var tdb = new CampaignTestDb();
        var orgId = Guid.NewGuid();
        var campaign = SeedCampaign(tdb.Db, orgId);
        var ranking = SeedRanking(tdb.Db, campaign.Id, 35.00m);
        await NewService(tdb.NewContext()).OverrideResultAsync(
            orgId, Guid.NewGuid(), "hr@isas.local", campaign.Id, ranking.SessionId, Set(72m, "Pass", "A"), default);

        using (var db = tdb.NewContext())
        {
            (await db.Campaigns.SingleAsync()).DeletedAt = DateTime.UtcNow;
            await db.SaveChangesAsync();
        }

        using var read = tdb.NewContext();
        Assert.Equal(0, await read.RankingOverrides.CountAsync());                       // DB13 chained filter
        Assert.Equal(1, await read.RankingOverrides.IgnoreQueryFilters().CountAsync());   // dòng vẫn tồn tại
        await Assert.ThrowsAsync<KeyNotFoundException>(() =>
            NewService(tdb.NewContext()).GetOverrideHistoryAsync(orgId, campaign.Id, ranking.SessionId, default));
    }

    // ── controller: claim email → actor_email ─────────────────────────────

    private static CampaignController NewController(CampaignDbContext db, Guid orgId, string? email)
    {
        var controller = new CampaignController(NewService(db), Mock.Of<ICvScreeningService>(), Mock.Of<ILogger<CampaignController>>());
        var claims = new List<Claim>
        {
            new(ClaimTypes.NameIdentifier, Guid.NewGuid().ToString()),
            new("org_id", orgId.ToString()),
            new("org_role", "OrgAdmin")
        };
        if (email is not null)
            claims.Add(new Claim("email", email));   // khoá literal "email" — MapInboundClaims=false (JwtService phát "email")
        controller.ControllerContext = new ControllerContext
        {
            HttpContext = new DefaultHttpContext { User = new ClaimsPrincipal(new ClaimsIdentity(claims, "Test")) }
        };
        return controller;
    }

    [Fact]
    public async Task Controller_ClaimEmail_VaoActorEmail()
    {
        using var tdb = new CampaignTestDb();
        var orgId = Guid.NewGuid();
        var campaign = SeedCampaign(tdb.Db, orgId);
        var ranking = SeedRanking(tdb.Db, campaign.Id, 35.00m);

        var result = await NewController(tdb.NewContext(), orgId, "hr@isas.local")
            .OverrideResult(campaign.Id, ranking.SessionId, Set(72m, "Pass", "A"), default);

        Assert.IsType<NoContentResult>(result);
        Assert.Equal("hr@isas.local", (await tdb.NewContext().RankingOverrides.SingleAsync()).ActorEmail);
    }

    [Fact]
    public async Task Controller_ThieuClaimEmail_ActorEmailNull_VanNoContent()
    {
        using var tdb = new CampaignTestDb();
        var orgId = Guid.NewGuid();
        var campaign = SeedCampaign(tdb.Db, orgId);
        var ranking = SeedRanking(tdb.Db, campaign.Id, 35.00m);

        var result = await NewController(tdb.NewContext(), orgId, email: null)
            .OverrideResult(campaign.Id, ranking.SessionId, Set(72m, "Pass", "A"), default);

        Assert.IsType<NoContentResult>(result);
        Assert.Null((await tdb.NewContext().RankingOverrides.SingleAsync()).ActorEmail);
    }

    [Fact]
    public async Task Controller_GetHistory_Ok_VaOrgKhac404()
    {
        using var tdb = new CampaignTestDb();
        var orgId = Guid.NewGuid();
        var campaign = SeedCampaign(tdb.Db, orgId);
        var ranking = SeedRanking(tdb.Db, campaign.Id, 35.00m);
        await NewService(tdb.NewContext()).OverrideResultAsync(
            orgId, Guid.NewGuid(), "hr@isas.local", campaign.Id, ranking.SessionId, Set(72m, "Pass", "A"), default);

        var ok = await NewController(tdb.NewContext(), orgId, "hr@isas.local").GetOverrideHistory(campaign.Id, ranking.SessionId, default);
        var body = Assert.IsType<OverrideHistoryResponse>(Assert.IsType<OkObjectResult>(ok.Result).Value);
        Assert.Single(body.Items);

        var other = await NewController(tdb.NewContext(), Guid.NewGuid(), "x@y.z").GetOverrideHistory(campaign.Id, ranking.SessionId, default);
        Assert.IsType<NotFoundObjectResult>(other.Result);
    }

    // ── proxy audio (phần 2) ──────────────────────────────────────────────

    [Fact]
    public async Task Audio_OrgKhac_404_KhongGoiInterview()
    {
        using var tdb = new CampaignTestDb();
        var orgId = Guid.NewGuid();
        var campaign = SeedCampaign(tdb.Db, orgId);
        var ranking = SeedRanking(tdb.Db, campaign.Id, 35.00m);
        var client = new Mock<ICampaignSessionClient>(MockBehavior.Strict);

        await Assert.ThrowsAsync<KeyNotFoundException>(() => NewService(tdb.NewContext(), client.Object)
            .GetSessionAnswerAudioAsync(Guid.NewGuid(), campaign.Id, ranking.SessionId, Guid.NewGuid(), default));
        client.VerifyNoOtherCalls();
    }

    [Fact]
    public async Task Audio_SessionChuaCham_404_KhongGoiInterview()
    {
        using var tdb = new CampaignTestDb();
        var orgId = Guid.NewGuid();
        var campaign = SeedCampaign(tdb.Db, orgId);
        var client = new Mock<ICampaignSessionClient>(MockBehavior.Strict);

        await Assert.ThrowsAsync<KeyNotFoundException>(() => NewService(tdb.NewContext(), client.Object)
            .GetSessionAnswerAudioAsync(orgId, campaign.Id, Guid.NewGuid(), Guid.NewGuid(), default));
        client.VerifyNoOtherCalls();
    }

    [Fact]
    public async Task Audio_InterviewKhongCo_404_ChuKhong502()
    {
        using var tdb = new CampaignTestDb();
        var orgId = Guid.NewGuid();
        var campaign = SeedCampaign(tdb.Db, orgId);
        var ranking = SeedRanking(tdb.Db, campaign.Id, 35.00m);
        var answerId = Guid.NewGuid();
        var client = new Mock<ICampaignSessionClient>();
        client.Setup(c => c.GetAnswerAudioAsync(ranking.SessionId, answerId, It.IsAny<CancellationToken>()))
            .ReturnsAsync((AnswerAudioContent?)null);

        var controller = new CampaignController(NewService(tdb.NewContext(), client.Object), Mock.Of<ICvScreeningService>(), Mock.Of<ILogger<CampaignController>>());
        controller.ControllerContext = new ControllerContext
        {
            HttpContext = new DefaultHttpContext
            {
                User = new ClaimsPrincipal(new ClaimsIdentity(new[]
                {
                    new Claim(ClaimTypes.NameIdentifier, Guid.NewGuid().ToString()),
                    new Claim("org_id", orgId.ToString())
                }, "Test"))
            }
        };

        var result = await controller.GetSessionAnswerAudio(campaign.Id, ranking.SessionId, answerId, default);
        Assert.IsType<NotFoundObjectResult>(result);
    }

    [Fact]
    public async Task Audio_TraStreamVaContentType_TuInterview()
    {
        using var tdb = new CampaignTestDb();
        var orgId = Guid.NewGuid();
        var campaign = SeedCampaign(tdb.Db, orgId);
        var ranking = SeedRanking(tdb.Db, campaign.Id, 35.00m);
        var answerId = Guid.NewGuid();
        var client = new Mock<ICampaignSessionClient>();
        client.Setup(c => c.GetAnswerAudioAsync(ranking.SessionId, answerId, It.IsAny<CancellationToken>()))
            .ReturnsAsync(new AnswerAudioContent(new MemoryStream([1, 2]), "audio/mp4"));

        var controller = new CampaignController(NewService(tdb.NewContext(), client.Object), Mock.Of<ICvScreeningService>(), Mock.Of<ILogger<CampaignController>>());
        controller.ControllerContext = new ControllerContext
        {
            HttpContext = new DefaultHttpContext
            {
                User = new ClaimsPrincipal(new ClaimsIdentity(new[]
                {
                    new Claim(ClaimTypes.NameIdentifier, Guid.NewGuid().ToString()),
                    new Claim("org_id", orgId.ToString())
                }, "Test"))
            }
        };

        var result = await controller.GetSessionAnswerAudio(campaign.Id, ranking.SessionId, answerId, default);
        var file = Assert.IsType<FileStreamResult>(result);
        Assert.Equal("audio/mp4", file.ContentType);
    }

    [Fact]
    public async Task Audio_InterviewLoi_502()
    {
        using var tdb = new CampaignTestDb();
        var orgId = Guid.NewGuid();
        var campaign = SeedCampaign(tdb.Db, orgId);
        var ranking = SeedRanking(tdb.Db, campaign.Id, 35.00m);
        var client = new Mock<ICampaignSessionClient>();
        client.Setup(c => c.GetAnswerAudioAsync(It.IsAny<Guid>(), It.IsAny<Guid>(), It.IsAny<CancellationToken>()))
            .ThrowsAsync(new DownstreamServiceException("Interview 500"));

        var controller = new CampaignController(NewService(tdb.NewContext(), client.Object), Mock.Of<ICvScreeningService>(), Mock.Of<ILogger<CampaignController>>());
        controller.ControllerContext = new ControllerContext
        {
            HttpContext = new DefaultHttpContext
            {
                User = new ClaimsPrincipal(new ClaimsIdentity(new[]
                {
                    new Claim(ClaimTypes.NameIdentifier, Guid.NewGuid().ToString()),
                    new Claim("org_id", orgId.ToString())
                }, "Test"))
            }
        };

        var result = await controller.GetSessionAnswerAudio(campaign.Id, ranking.SessionId, Guid.NewGuid(), default);
        Assert.Equal(StatusCodes.Status502BadGateway, Assert.IsType<ObjectResult>(result).StatusCode);
    }
}
