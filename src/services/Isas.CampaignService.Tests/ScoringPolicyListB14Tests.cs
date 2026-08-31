using System.Reflection;
using System.Security.Claims;
using Isas.CampaignService.Controllers;
using Isas.CampaignService.DTOs;
using Isas.CampaignService.Models;
using Isas.CampaignService.Services;
using Isas.Shared.Scoring;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc;
using Microsoft.Extensions.Logging;
using Moq;
using Xunit;

namespace Isas.CampaignService.Tests;

/// <summary>
/// SCP1-B14 — GET /campaign/{id}/scoring-policies: liệt kê các version chính sách chấm ĐÃ TẠO cho
/// campaign (KHÔNG gồm mẫu hệ thống). Sắp Kind rồi Version GIẢM DẦN. ?kind= lọc tuỳ chọn. CHỈ ĐỌC.
/// </summary>
public class ScoringPolicyListB14Tests
{
    private sealed record Env(CampaignController Controller, CampaignTestDb Db, Guid OrgId, Guid CampaignId);

    private static Env Setup()
    {
        var tdb = new CampaignTestDb();
        var orgId = Guid.NewGuid();
        var campaign = CampaignTestDb.NewCampaign(orgId, CampaignStatus.Draft);
        tdb.Db.Campaigns.Add(campaign);
        tdb.Db.SaveChanges();

        var controller = new CampaignController(
            Mock.Of<ICampaignService>(),
            Mock.Of<ICvScreeningService>(),
            Mock.Of<ILogger<CampaignController>>(),
            policies: new ScoringPolicyService(tdb.NewContext()));

        var claims = new List<Claim>
        {
            new(ClaimTypes.NameIdentifier, Guid.NewGuid().ToString()),
            new("org_id", orgId.ToString()),
            new("org_role", "OrgAdmin"),
        };
        controller.ControllerContext = new ControllerContext
        {
            HttpContext = new DefaultHttpContext { User = new ClaimsPrincipal(new ClaimsIdentity(claims, "Test")) }
        };

        return new Env(controller, tdb, orgId, campaign.Id);
    }

    private static void AddPolicy(
        CampaignTestDb tdb, Guid campaignId, ScoringExpressionKind kind, int version, string name)
    {
        using var w = tdb.NewContext();
        w.ScoringPolicies.Add(new ScoringPolicy
        {
            Id = Guid.NewGuid(),
            CampaignId = campaignId,
            Kind = kind,
            Version = version,
            EngineVersion = ScoringEngine.Version,
            Name = name,
            Expression = "weighted_avg_pct",
            PassScorePct = kind == ScoringExpressionKind.Interview ? 60 : null,
            CreatedAt = DateTime.UtcNow,
            CreatedBy = Guid.NewGuid(),
        });
        w.SaveChanges();
    }

    private static async Task<IReadOnlyList<ScoringPolicyResponse>> List(
        CampaignController c, Guid campaignId, string? kind = null)
    {
        var action = await c.ListScoringPolicies(campaignId, kind, default);
        var ok = Assert.IsType<OkObjectResult>(action.Result);
        return Assert.IsAssignableFrom<IReadOnlyList<ScoringPolicyResponse>>(ok.Value);
    }

    // ── danh sách + thứ tự (Kind rồi Version GIẢM DẦN) ────────────────────────────────────────
    [Fact]
    public async Task Tra_du_moi_version_sap_Kind_roi_Version_giam_dan()
    {
        var e = Setup();
        using var _d = e.Db;
        AddPolicy(e.Db, e.CampaignId, ScoringExpressionKind.Interview, 1, "iv1");
        AddPolicy(e.Db, e.CampaignId, ScoringExpressionKind.Interview, 2, "iv2");
        AddPolicy(e.Db, e.CampaignId, ScoringExpressionKind.Interview, 3, "iv3");
        AddPolicy(e.Db, e.CampaignId, ScoringExpressionKind.CvScreening, 1, "cv1");
        // campaign KHÁC — phải KHÔNG lọt vào (bỏ vế p.CampaignId == campaignId sẽ kéo nó + mẫu vào).
        AddPolicy(e.Db, Guid.NewGuid(), ScoringExpressionKind.Interview, 9, "khac");

        var list = await List(e.Controller, e.CampaignId);

        Assert.Equal(
            new[]
            {
                ("Interview", 3),
                ("Interview", 2),
                ("Interview", 1),
                ("CvScreening", 1),
            },
            list.Select(p => (p.Kind, p.Version)).ToArray());
    }

    [Fact]
    public async Task Loc_kind_chi_tra_dung_loai()
    {
        var e = Setup();
        using var _d = e.Db;
        AddPolicy(e.Db, e.CampaignId, ScoringExpressionKind.Interview, 1, "iv1");
        AddPolicy(e.Db, e.CampaignId, ScoringExpressionKind.Interview, 2, "iv2");
        AddPolicy(e.Db, e.CampaignId, ScoringExpressionKind.CvScreening, 1, "cv1");

        var iv = await List(e.Controller, e.CampaignId, kind: "Interview");
        Assert.Equal(new[] { 2, 1 }, iv.Select(p => p.Version).ToArray());
        Assert.All(iv, p => Assert.Equal("Interview", p.Kind));

        var cv = await List(e.Controller, e.CampaignId, kind: "CvScreening");
        Assert.Equal(new[] { ("CvScreening", 1) }, cv.Select(p => (p.Kind, p.Version)).ToArray());
    }

    // ── campaign chưa có policy nào → [] (KHÔNG 404) ─────────────────────────────────────────
    [Fact]
    public async Task Campaign_chua_co_policy_tra_mang_rong_khong_404()
    {
        var e = Setup();
        using var _d = e.Db;

        var list = await List(e.Controller, e.CampaignId);
        Assert.Empty(list);
    }

    // ── campaign ngoài org → 404 (BK15) ─────────────────────────────────────────────────────
    [Fact]
    public async Task Campaign_ngoai_org_tra_404()
    {
        var e = Setup();
        using var _d = e.Db;
        // campaign thuộc org KHÁC, có policy → vẫn phải 404 (không lộ tồn tại).
        var otherCampaign = CampaignTestDb.NewCampaign(Guid.NewGuid(), CampaignStatus.Draft);
        {
            using var w = e.Db.NewContext();
            w.Campaigns.Add(otherCampaign);
            w.SaveChanges();
        }
        AddPolicy(e.Db, otherCampaign.Id, ScoringExpressionKind.Interview, 1, "iv1");

        var action = await e.Controller.ListScoringPolicies(otherCampaign.Id, null, default);
        Assert.IsType<NotFoundResult>(action.Result);
    }

    // ── kind rác → 400 ─────────────────────────────────────────────────────────────────────
    [Theory]
    [InlineData("Foo")]
    [InlineData("interview")]   // phân biệt hoa/thường
    [InlineData("CV")]
    public async Task Kind_rac_tra_400(string kind)
    {
        var e = Setup();
        using var _d = e.Db;

        var action = await e.Controller.ListScoringPolicies(e.CampaignId, kind, default);
        var bad = Assert.IsType<BadRequestObjectResult>(action.Result);
        Assert.Contains("Interview", bad.Value!.ToString());
    }

    // ── phân quyền: [Authorize(Roles="Employer")] trên action ───────────────────────────────
    [Fact]
    public void ListScoringPolicies_yeu_cau_role_Employer()
    {
        var attr = typeof(CampaignController)
            .GetMethod(nameof(CampaignController.ListScoringPolicies))!
            .GetCustomAttribute<AuthorizeAttribute>();
        Assert.NotNull(attr);
        Assert.Equal("Employer", attr!.Roles);
    }
}
