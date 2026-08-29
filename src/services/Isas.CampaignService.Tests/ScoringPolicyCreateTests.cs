using System.Security.Claims;
using System.Text.Json;
using Isas.CampaignService.Controllers;
using Isas.CampaignService.DTOs;
using Isas.CampaignService.Models;
using Isas.CampaignService.Services;
using Isas.Shared.Scoring;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging;
using Moq;
using Xunit;

namespace Isas.CampaignService.Tests;

/// <summary>
/// SCP1-B4 — POST /campaign/{id}/scoring-policies: employer CHÉP mẫu về campaign rồi chỉnh.
/// CHÉP giá trị, KHÔNG tham chiếu sống (CAMP-20) · con trỏ campaigns.*_policy_version · HĐ-6.
/// </summary>
public class ScoringPolicyCreateTests
{
    private sealed record Env(
        CampaignController Controller, CampaignTestDb Db, Guid OrgId, Guid CampaignId, Guid ActorUserId);

    private static Env Setup(
        CampaignStatus status = CampaignStatus.Draft,
        string orgRole = "OrgAdmin",
        bool withOrgClaim = true)
    {
        var tdb = new CampaignTestDb();
        var orgId = Guid.NewGuid();
        var actor = Guid.NewGuid();
        var campaign = CampaignTestDb.NewCampaign(orgId, status);
        tdb.Db.Campaigns.Add(campaign);
        tdb.Db.SaveChanges();

        var controller = new CampaignController(
            Mock.Of<ICampaignService>(),
            Mock.Of<ICvScreeningService>(),
            Mock.Of<ILogger<CampaignController>>(),
            policies: new ScoringPolicyService(tdb.NewContext()));

        var claims = new List<Claim> { new(ClaimTypes.NameIdentifier, actor.ToString()) };
        if (withOrgClaim) claims.Add(new Claim("org_id", orgId.ToString()));
        if (orgRole is not null) claims.Add(new Claim("org_role", orgRole));
        controller.ControllerContext = new ControllerContext
        {
            HttpContext = new DefaultHttpContext { User = new ClaimsPrincipal(new ClaimsIdentity(claims, "Test")) }
        };

        return new Env(controller, tdb, orgId, campaign.Id, actor);
    }

    private static CreateScoringPolicyRequest Req(
        string kind = "Interview", string name = "Bản của HR",
        string expr = "weighted_avg_pct", int? pass = 55, Guid? sourceTemplateId = null)
        => new() { Kind = kind, Name = name, Expression = expr, PassScorePct = pass, SourceTemplateId = sourceTemplateId };

    private static async Task<ScoringPolicyResponse> Created(CampaignController c, Guid campaignId, CreateScoringPolicyRequest req)
    {
        var action = await c.CreateScoringPolicy(campaignId, req, default);
        var ok = Assert.IsType<OkObjectResult>(action.Result);
        return Assert.IsType<ScoringPolicyResponse>(ok.Value);
    }

    private static Guid TemplateId(CampaignTestDb tdb, ScoringExpressionKind kind, string name)
        => tdb.Db.ScoringPolicies.AsNoTracking()
            .Single(p => p.CampaignId == null && p.Kind == kind && p.Name == name).Id;

    // ── tạo từ biểu thức tự gõ ────────────────────────────────────────────────────────────────
    [Fact]
    public async Task Tao_tu_bieu_thuc_tu_go_version_1_va_tro_con_tro()
    {
        var e = Setup();
        using var _d = e.Db;

        var r = await Created(e.Controller, e.CampaignId,
            Req(name: "Chấm nghiêm", expr: "if(min_pct < 40, min_pct, weighted_avg_pct)", pass: 65));

        Assert.Equal(1, r.Version);
        Assert.Equal(ScoringEngine.Version, r.EngineVersion);
        Assert.Equal("Chấm nghiêm", r.Name);
        Assert.Equal("if(min_pct < 40, min_pct, weighted_avg_pct)", r.Expression);
        Assert.Equal(65, r.PassScorePct);
        Assert.Null(r.SourceTemplateId);
        Assert.Equal(e.ActorUserId, r.CreatedBy);

        using var db = e.Db.NewContext();
        var camp = await db.Campaigns.SingleAsync(x => x.Id == e.CampaignId);
        Assert.Equal(1, camp.InterviewPolicyVersion);
        Assert.Null(camp.CvPolicyVersion);
        Assert.Equal(1, await db.ScoringPolicies.CountAsync(p => p.CampaignId == e.CampaignId));
    }

    [Fact]
    public async Task Tao_version_2_bump_va_doi_con_tro()
    {
        var e = Setup();
        using var _d = e.Db;

        await Created(e.Controller, e.CampaignId, Req(name: "v1"));
        var r2 = await Created(e.Controller, e.CampaignId, Req(name: "v2", expr: "avg_pct"));

        Assert.Equal(2, r2.Version);
        using var db = e.Db.NewContext();
        Assert.Equal(2, (await db.Campaigns.SingleAsync(x => x.Id == e.CampaignId)).InterviewPolicyVersion);
    }

    [Fact]
    public async Task Con_tro_Interview_va_Cv_doc_lap()
    {
        var e = Setup();
        using var _d = e.Db;

        await Created(e.Controller, e.CampaignId, Req(kind: "Interview", name: "iv"));
        await Created(e.Controller, e.CampaignId,
            Req(kind: "CvScreening", name: "cv", expr: "100 * strong_count / need_count", pass: 50));

        using var db = e.Db.NewContext();
        var camp = await db.Campaigns.SingleAsync(x => x.Id == e.CampaignId);
        Assert.Equal(1, camp.InterviewPolicyVersion);
        Assert.Equal(1, camp.CvPolicyVersion);
        // version đánh số RIÊNG theo (campaign, kind) — cả hai đều version 1.
        Assert.Equal(1, await db.ScoringPolicies.CountAsync(p => p.CampaignId == e.CampaignId && p.Kind == ScoringExpressionKind.Interview));
        Assert.Equal(1, await db.ScoringPolicies.CountAsync(p => p.CampaignId == e.CampaignId && p.Kind == ScoringExpressionKind.CvScreening));
    }

    // ── CHÉP mẫu, KHÔNG tham chiếu sống ───────────────────────────────────────────────────────
    [Fact]
    public async Task Chep_mau_giu_provenance_nhung_gia_tri_doc_lap()
    {
        var e = Setup();
        using var _d = e.Db;
        var tid = TemplateId(e.Db, ScoringExpressionKind.Interview, "Không bù trừ");

        // FE chọn mẫu → editor điền sẵn biểu thức của mẫu → user gửi lên (chưa sửa).
        var r = await Created(e.Controller, e.CampaignId,
            Req(name: "Từ mẫu Không bù trừ",
                expr: "if(min_pct < 40, min_pct, weighted_avg_pct)",
                pass: 60, sourceTemplateId: tid));

        Assert.Equal(tid, r.SourceTemplateId);   // dấu vết provenance được giữ
        Assert.Equal("if(min_pct < 40, min_pct, weighted_avg_pct)", r.Expression);
    }

    [Fact]
    public async Task Chep_mau_roi_sua_mau_goc_campaign_KHONG_doi()
    {
        var e = Setup();
        using var _d = e.Db;
        var tid = TemplateId(e.Db, ScoringExpressionKind.Interview, "Như hiện nay");

        var created = await Created(e.Controller, e.CampaignId,
            Req(name: "Bản campaign", expr: "weighted_avg_pct", pass: 60, sourceTemplateId: tid));

        // Sửa mẫu GỐC (name là trường duy nhất mẫu cho sửa — expression/pass bị PropertySaveBehavior.Throw).
        {
            using var w = e.Db.NewContext();
            var template = await w.ScoringPolicies.SingleAsync(p => p.Id == tid);
            template.Name = "MẪU ĐÃ ĐỔI TÊN";
            template.Description = "mô tả mới của admin";
            await w.SaveChangesAsync();
        }

        // Bản của campaign KHÔNG đổi theo — đọc lại từ context sạch, KHÔNG join gì tới mẫu.
        using var db = e.Db.NewContext();
        var policy = await db.ScoringPolicies.SingleAsync(p => p.Id == created.Id);
        Assert.Equal("Bản campaign", policy.Name);
        Assert.Equal("weighted_avg_pct", policy.Expression);
        Assert.Equal(60, policy.PassScorePct);
        Assert.Equal(ScoringEngine.Version, policy.EngineVersion);
        Assert.Equal(tid, policy.SourceTemplateId);   // vẫn trỏ tới mẫu — nhưng chỉ là DẤU, không deref

        // mẫu gốc thì ĐÃ đổi — chứng minh phép sửa ở trên có tác dụng thật.
        var template2 = await db.ScoringPolicies.SingleAsync(p => p.Id == tid);
        Assert.Equal("MẪU ĐÃ ĐỔI TÊN", template2.Name);
    }

    [Fact]
    public async Task sourceTemplateId_khong_phai_mau_hoac_sai_kind_tra_400()
    {
        var e = Setup();
        using var _d = e.Db;

        // (a) id của một policy campaign (không phải mẫu)
        var own = await Created(e.Controller, e.CampaignId, Req(name: "own"));
        var a = await e.Controller.CreateScoringPolicy(e.CampaignId,
            Req(name: "x", sourceTemplateId: own.Id), default);
        Assert.IsType<BadRequestObjectResult>(a.Result);

        // (b) mẫu CvScreening dùng cho policy Interview
        var cvTid = TemplateId(e.Db, ScoringExpressionKind.CvScreening, "Như hiện nay");
        var b = await e.Controller.CreateScoringPolicy(e.CampaignId,
            Req(kind: "Interview", name: "y", sourceTemplateId: cvTid), default);
        Assert.IsType<BadRequestObjectResult>(b.Result);
    }

    // ── HĐ-6 phân quyền ──────────────────────────────────────────────────────────────────────
    [Fact]
    public async Task HrMember_khi_Draft_thi_200()
    {
        var e = Setup(status: CampaignStatus.Draft, orgRole: "HrMember");
        using var _d = e.Db;
        var r = await Created(e.Controller, e.CampaignId, Req());
        Assert.Equal(1, r.Version);
    }

    [Fact]
    public async Task HrMember_khi_Active_thi_403()
    {
        var e = Setup(status: CampaignStatus.Active, orgRole: "HrMember");
        using var _d = e.Db;

        var action = await e.Controller.CreateScoringPolicy(e.CampaignId, Req(), default);

        var obj = Assert.IsType<ObjectResult>(action.Result);
        Assert.Equal(StatusCodes.Status403Forbidden, obj.StatusCode);
    }

    [Fact]
    public async Task OrgAdmin_khi_Active_va_chua_ai_duoc_cham_thi_200()
    {
        var e = Setup(status: CampaignStatus.Active, orgRole: "OrgAdmin");
        using var _d = e.Db;
        var r = await Created(e.Controller, e.CampaignId, Req());
        Assert.Equal(1, r.Version);
    }

    // ── CẤM B4: đã có người được chấm → 409 (thuộc B8) ───────────────────────────────────────
    [Fact]
    public async Task Interview_da_co_ranking_thi_409_POLICY_NEEDS_PREVIEW()
    {
        var e = Setup(status: CampaignStatus.Active, orgRole: "OrgAdmin");
        using var _d = e.Db;
        {
            using var w = e.Db.NewContext();
            w.CampaignRankings.Add(new CampaignRanking
            {
                Id = Guid.NewGuid(), CampaignId = e.CampaignId, CandidateId = Guid.NewGuid(),
                SessionId = Guid.NewGuid(), TotalScore = 72m, UpdatedAt = DateTime.UtcNow,
            });
            await w.SaveChangesAsync();
        }

        var action = await e.Controller.CreateScoringPolicy(e.CampaignId, Req(kind: "Interview"), default);

        var conflict = Assert.IsType<ConflictObjectResult>(action.Result);
        Assert.Contains("POLICY_NEEDS_PREVIEW", JsonSerializer.Serialize(conflict.Value));
    }

    [Fact]
    public async Task CvScreening_da_co_diem_khop_thi_409()
    {
        var e = Setup(status: CampaignStatus.Active, orgRole: "OrgAdmin");
        using var _d = e.Db;
        {
            using var w = e.Db.NewContext();
            w.CvSubmissions.Add(new CvSubmission
            {
                Id = Guid.NewGuid(), CampaignId = e.CampaignId, Email = "a@x.com", CvParsedText = "CV",
                ParseStatus = CvParseStatus.Done, Status = CvSubmissionStatus.Analyzed,
                OverallMatchScore = 80, CreatedAt = DateTime.UtcNow, UpdatedAt = DateTime.UtcNow,
            });
            await w.SaveChangesAsync();
        }

        // Interview kind vẫn tạo được (chưa có ranking) — chỉ CvScreening bị chặn.
        var iv = await e.Controller.CreateScoringPolicy(e.CampaignId, Req(kind: "Interview"), default);
        Assert.IsType<OkObjectResult>(iv.Result);

        var cv = await e.Controller.CreateScoringPolicy(e.CampaignId,
            Req(kind: "CvScreening", expr: "100 * strong_count / need_count", pass: 50), default);
        Assert.IsType<ConflictObjectResult>(cv.Result);
    }

    // ── khác ────────────────────────────────────────────────────────────────────────────────
    [Fact]
    public async Task Campaign_Closed_thi_409()
    {
        var e = Setup(status: CampaignStatus.Closed, orgRole: "OrgAdmin");
        using var _d = e.Db;
        var action = await e.Controller.CreateScoringPolicy(e.CampaignId, Req(), default);
        Assert.IsType<ConflictObjectResult>(action.Result);
    }

    [Fact]
    public async Task Bieu_thuc_hong_tra_400_kem_errors_HĐ2()
    {
        var e = Setup();
        using var _d = e.Db;

        var action = await e.Controller.CreateScoringPolicy(e.CampaignId,
            Req(name: "x", expr: "weighted_avg_pct * "), default);

        var bad = Assert.IsType<BadRequestObjectResult>(action.Result);
        Assert.Contains("SYNTAX_ERROR", JsonSerializer.Serialize(bad.Value));
        // Không lưu gì khi biểu thức hỏng.
        using var db = e.Db.NewContext();
        Assert.Equal(0, await db.ScoringPolicies.CountAsync(p => p.CampaignId == e.CampaignId));
        Assert.Null((await db.Campaigns.SingleAsync(x => x.Id == e.CampaignId)).InterviewPolicyVersion);
    }

    [Fact]
    public async Task Campaign_ngoai_org_tra_404()
    {
        var e = Setup();
        using var _d = e.Db;
        var action = await e.Controller.CreateScoringPolicy(Guid.NewGuid(), Req(), default);
        Assert.IsType<NotFoundResult>(action.Result);
    }

    [Theory]
    [InlineData(null, "weighted_avg_pct")]   // thiếu kind
    [InlineData("Foo", "weighted_avg_pct")]  // kind lạ
    public async Task kind_sai_tra_400(string? kind, string expr)
    {
        var e = Setup();
        using var _d = e.Db;
        var action = await e.Controller.CreateScoringPolicy(e.CampaignId,
            new CreateScoringPolicyRequest { Kind = kind, Name = "x", Expression = expr }, default);
        Assert.IsType<BadRequestObjectResult>(action.Result);
    }

    [Fact]
    public async Task Thieu_name_tra_400()
    {
        var e = Setup();
        using var _d = e.Db;
        var action = await e.Controller.CreateScoringPolicy(e.CampaignId,
            new CreateScoringPolicyRequest { Kind = "Interview", Name = "  ", Expression = "weighted_avg_pct" }, default);
        Assert.IsType<BadRequestObjectResult>(action.Result);
    }

    [Fact]
    public async Task Policy_vua_tao_van_bat_bien_khong_UPDATE_duoc_expression()
    {
        var e = Setup();
        using var _d = e.Db;
        var created = await Created(e.Controller, e.CampaignId, Req());

        using var db = e.Db.NewContext();
        var p = await db.ScoringPolicies.SingleAsync(x => x.Id == created.Id);
        p.Expression = "avg_pct";
        await Assert.ThrowsAsync<InvalidOperationException>(() => db.SaveChangesAsync());
    }
}
