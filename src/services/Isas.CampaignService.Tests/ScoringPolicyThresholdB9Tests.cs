using System.Security.Claims;
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

using CampaignSvc = Isas.CampaignService.Services.CampaignService;

namespace Isas.CampaignService.Tests;

/// <summary>
/// SCP1-B9 — ngưỡng đạt do CHÍNH SÁCH CHẤM quyết định (đóng quyết định BL).
///
/// Trước B9: <c>ScoringPolicy.PassScorePct</c> được ghi + ghim nhưng KHÔNG code nào đọc; Pass/Fail
/// thật vẫn tính bằng <c>campaign.PassScorePct</c> (E5). ⇒ employer gõ "đạt từ 70%" trong trình soạn
/// chính sách mà bảng vẫn chấm ở con số cũ. B9 đồng bộ ngưỡng của chính sách VÀO
/// <c>campaign.PassScorePct</c> ở mọi chỗ con trỏ Interview dịch (create-!hasScored · apply), chặn
/// ghi chồng câm ở PUT /campaign, và bỏ ngưỡng khỏi 2 mẫu CvScreening (CV không có đạt/trượt).
/// </summary>
public class ScoringPolicyThresholdB9Tests
{
    // ── helpers ──────────────────────────────────────────────────────────────────────────────
    private static IEntitlementClient Entitlements()
    {
        var client = new Mock<IEntitlementClient>();
        client.Setup(x => x.ResolveOrgAsync(It.IsAny<Guid>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(new CampaignEntitlement("test", "business", 1, 10, 200, true, true, true));
        return client.Object;
    }

    private static CampaignSvc NewCampaignService(CampaignDbContext db) =>
        new(db, Mock.Of<IFileService>(), Mock.Of<ILogger<CampaignSvc>>(), Mock.Of<IParserService>(),
            Mock.Of<ICriteriaSuggester>(), Mock.Of<IInvitationEmailPublisher>(), entitlements: Entitlements());

    private static CampaignController Controller(CampaignTestDb tdb, Guid orgId, string orgRole = "OrgAdmin")
    {
        var c = new CampaignController(
            Mock.Of<ICampaignService>(), Mock.Of<ICvScreeningService>(), Mock.Of<ILogger<CampaignController>>(),
            policies: new ScoringPolicyService(tdb.NewContext()));
        var claims = new List<Claim>
        {
            new(ClaimTypes.NameIdentifier, Guid.NewGuid().ToString()),
            new("org_id", orgId.ToString()),
            new("org_role", orgRole),
        };
        c.ControllerContext = new ControllerContext
        {
            HttpContext = new DefaultHttpContext { User = new ClaimsPrincipal(new ClaimsIdentity(claims, "Test")) }
        };
        return c;
    }

    // 2 tiêu chí weight 0.5, maxScore 5 ⇒ weighted_avg_pct = mean(pct).
    private static ScoringInputsSnapshot Bag(decimal pctA, decimal pctB)
        => new(
            new[]
            {
                new CriterionInputSnapshot("A", pctA, 0.5m, 5),
                new CriterionInputSnapshot("B", pctB, 0.5m, 5),
            }, 8, 10);

    private static Guid SeedRanking(CampaignTestDb tdb, Guid campaignId, decimal total, ScoringInputsSnapshot bag)
    {
        var cid = Guid.NewGuid();
        tdb.Db.CampaignRankings.Add(new CampaignRanking
        {
            Id = Guid.NewGuid(), CampaignId = campaignId, CandidateId = cid, SessionId = Guid.NewGuid(),
            TotalScore = total, ScoringInputs = bag, UpdatedAt = DateTime.UtcNow,
        });
        tdb.Db.SaveChanges();
        return cid;
    }

    private static async Task<ScoringPolicyResponse> CreatePolicy(
        CampaignController c, Guid campaignId, string kind, string expr, int? pass)
    {
        var action = await c.CreateScoringPolicy(campaignId,
            new CreateScoringPolicyRequest { Kind = kind, Name = "Bản B9", Expression = expr, PassScorePct = pass },
            default);
        return Assert.IsType<ScoringPolicyResponse>(Assert.IsType<OkObjectResult>(action.Result).Value);
    }

    private static async Task Apply(CampaignController c, Guid campaignId, Guid policyId, string expr, int? pass)
    {
        var fp = ScoringPolicyFingerprint.Compute(expr, pass, ScoringEngine.Version);
        var action = await c.ApplyScoringPolicy(campaignId, policyId,
            new ApplyScoringPolicyRequest { Fingerprint = fp }, default);
        Assert.IsType<OkObjectResult>(action.Result);
    }

    // ── (a) apply policy CÓ ngưỡng → campaign.PassScorePct đổi → hàng kết quả đổi Pass/Fail ───
    [Fact]
    public async Task Apply_policy_co_nguong_thi_bang_ket_qua_doi_pass_fail()
    {
        using var tdb = new CampaignTestDb();
        var org = Guid.NewGuid();
        var camp = CampaignTestDb.NewCampaign(org, CampaignStatus.Active);
        camp.PassScorePct = 60;                       // ngưỡng HR đặt: 70 điểm = ĐẠT
        tdb.Db.Campaigns.Add(camp);
        tdb.Db.SaveChanges();
        SeedRanking(tdb, camp.Id, total: 70m, bag: Bag(70m, 70m));   // weighted_avg_pct = 70

        var ctrl = Controller(tdb, org);
        // Cùng biểu thức "weighted_avg_pct" ⇒ điểm KHÔNG đổi (70); chỉ ngưỡng đổi 60 → 80.
        var policy = await CreatePolicy(ctrl, camp.Id, "Interview", "weighted_avg_pct", pass: 80);
        await Apply(ctrl, camp.Id, policy.Id, "weighted_avg_pct", pass: 80);

        // Cột campaign đồng bộ...
        Assert.Equal(80, (await tdb.NewContext().Campaigns.SingleAsync(x => x.Id == camp.Id)).PassScorePct);

        // ...VÀ đường Pass/Fail (E5) chấm ở con số mới: 70 < 80 ⇒ Fail (trước là Pass ở ngưỡng 60).
        var results = await NewCampaignService(tdb.NewContext()).GetCampaignResultsAsync(org, camp.Id, default);
        Assert.Equal(80, results.PassScorePct);
        Assert.Equal("Fail", Assert.Single(results.Results).Result);
    }

    // ── (b) apply policy ngưỡng NULL → campaign.PassScorePct GIỮ NGUYÊN ───────────────────────
    [Fact]
    public async Task Apply_policy_nguong_null_thi_campaign_pass_score_giu_nguyen()
    {
        using var tdb = new CampaignTestDb();
        var org = Guid.NewGuid();
        var camp = CampaignTestDb.NewCampaign(org, CampaignStatus.Active);
        camp.PassScorePct = 55;
        tdb.Db.Campaigns.Add(camp);
        tdb.Db.SaveChanges();
        SeedRanking(tdb, camp.Id, total: 70m, bag: Bag(70m, 70m));

        var ctrl = Controller(tdb, org);
        var policy = await CreatePolicy(ctrl, camp.Id, "Interview", "min_pct", pass: null);
        await Apply(ctrl, camp.Id, policy.Id, "min_pct", pass: null);

        // Con trỏ dời nhưng ngưỡng HR đã đặt KHÔNG bị đụng (ngữ nghĩa null của E5 = "HR quyết tay").
        var after = await tdb.NewContext().Campaigns.SingleAsync(x => x.Id == camp.Id);
        Assert.Equal(policy.Version, after.InterviewPolicyVersion);
        Assert.Equal(55, after.PassScorePct);
    }

    // ── (c) create khi CHƯA ai chấm → cũng đồng bộ ───────────────────────────────────────────
    [Fact]
    public async Task Create_chua_ai_cham_dong_bo_nguong_vao_campaign()
    {
        using var tdb = new CampaignTestDb();
        var org = Guid.NewGuid();
        var camp = CampaignTestDb.NewCampaign(org, CampaignStatus.Draft);
        camp.PassScorePct = 50;
        tdb.Db.Campaigns.Add(camp);
        tdb.Db.SaveChanges();

        var ctrl = Controller(tdb, org);
        var policy = await CreatePolicy(ctrl, camp.Id, "Interview", "weighted_avg_pct", pass: 72);

        var after = await tdb.NewContext().Campaigns.SingleAsync(x => x.Id == camp.Id);
        Assert.Equal(policy.Version, after.InterviewPolicyVersion);
        Assert.Equal(72, after.PassScorePct);   // ngưỡng của chính sách thắng giá trị cũ (50)
    }

    [Fact]
    public async Task Create_chua_ai_cham_nguong_null_khong_dung_campaign()
    {
        using var tdb = new CampaignTestDb();
        var org = Guid.NewGuid();
        var camp = CampaignTestDb.NewCampaign(org, CampaignStatus.Draft);
        camp.PassScorePct = 50;
        tdb.Db.Campaigns.Add(camp);
        tdb.Db.SaveChanges();

        var ctrl = Controller(tdb, org);
        await CreatePolicy(ctrl, camp.Id, "Interview", "min_pct", pass: null);

        Assert.Equal(50, (await tdb.NewContext().Campaigns.SingleAsync(x => x.Id == camp.Id)).PassScorePct);
    }

    // ── (d) PUT /campaign: giá trị KHÁC ngưỡng của chính sách → 400; đúng bằng → 200 ───────────
    [Fact]
    public async Task Put_pass_score_khac_nguong_chinh_sach_thi_400()
    {
        using var tdb = new CampaignTestDb();
        var org = Guid.NewGuid();
        var camp = CampaignTestDb.NewCampaign(org, CampaignStatus.Active);
        camp.PassScorePct = 70;
        camp.InterviewPolicyVersion = 1;
        tdb.Db.Campaigns.Add(camp);
        tdb.Db.ScoringPolicies.Add(new ScoringPolicy
        {
            Id = Guid.NewGuid(), CampaignId = camp.Id, Kind = ScoringExpressionKind.Interview, Version = 1,
            EngineVersion = ScoringEngine.Version, Name = "v1", Expression = "weighted_avg_pct",
            PassScorePct = 70, CreatedAt = DateTime.UtcNow, CreatedBy = Guid.NewGuid(),
        });
        tdb.Db.SaveChanges();

        var svc = NewCampaignService(tdb.NewContext());
        var ex = await Assert.ThrowsAsync<ArgumentException>(() =>
            svc.UpdateCampaignAsync(org, org, camp.Id,
                new UpdateCampaignRequest { Title = camp.Title, PassScorePct = 55 }, default));
        Assert.Contains("chính sách chấm phỏng vấn v1", ex.Message);

        // KHÔNG ghi đè.
        Assert.Equal(70, (await tdb.NewContext().Campaigns.SingleAsync(x => x.Id == camp.Id)).PassScorePct);
    }

    [Fact]
    public async Task Put_pass_score_dung_bang_nguong_chinh_sach_thi_200()
    {
        using var tdb = new CampaignTestDb();
        var org = Guid.NewGuid();
        var camp = CampaignTestDb.NewCampaign(org, CampaignStatus.Active);
        camp.PassScorePct = 70;
        camp.InterviewPolicyVersion = 1;
        tdb.Db.Campaigns.Add(camp);
        tdb.Db.ScoringPolicies.Add(new ScoringPolicy
        {
            Id = Guid.NewGuid(), CampaignId = camp.Id, Kind = ScoringExpressionKind.Interview, Version = 1,
            EngineVersion = ScoringEngine.Version, Name = "v1", Expression = "weighted_avg_pct",
            PassScorePct = 70, CreatedAt = DateTime.UtcNow, CreatedBy = Guid.NewGuid(),
        });
        tdb.Db.SaveChanges();

        // FE echo lại cả form (PassScorePct = 70 = giá trị hiện hành) ⇒ KHÔNG lỗi.
        var res = await NewCampaignService(tdb.NewContext()).UpdateCampaignAsync(org, org, camp.Id,
            new UpdateCampaignRequest { Title = camp.Title, PassScorePct = 70 }, default);
        Assert.Equal(70, res.PassScorePct);
    }

    [Fact]
    public async Task Put_campaign_chua_co_policy_thi_duong_ghi_cu_nguyen_ven()
    {
        using var tdb = new CampaignTestDb();
        var org = Guid.NewGuid();
        var camp = CampaignTestDb.NewCampaign(org, CampaignStatus.Active);
        camp.PassScorePct = 60;   // InterviewPolicyVersion = null (chưa có policy)
        tdb.Db.Campaigns.Add(camp);
        tdb.Db.SaveChanges();

        var res = await NewCampaignService(tdb.NewContext()).UpdateCampaignAsync(org, org, camp.Id,
            new UpdateCampaignRequest { Title = camp.Title, PassScorePct = 85 }, default);
        Assert.Equal(85, res.PassScorePct);
    }

    // ── (e) tạo policy CvScreening kèm passScorePct → 400 ────────────────────────────────────
    [Fact]
    public async Task Create_CvScreening_kem_passScorePct_thi_400()
    {
        using var tdb = new CampaignTestDb();
        var org = Guid.NewGuid();
        var camp = CampaignTestDb.NewCampaign(org, CampaignStatus.Draft);
        tdb.Db.Campaigns.Add(camp);
        tdb.Db.SaveChanges();

        var ctrl = Controller(tdb, org);
        var action = await ctrl.CreateScoringPolicy(camp.Id, new CreateScoringPolicyRequest
        {
            Kind = "CvScreening", Name = "x", Expression = "100 * strong_count / need_count", PassScorePct = 50,
        }, default);

        Assert.IsType<BadRequestObjectResult>(action.Result);

        // Không lưu gì.
        Assert.Equal(0, await tdb.NewContext().ScoringPolicies.CountAsync(p => p.CampaignId == camp.Id));
    }

    [Fact]
    public async Task Create_CvScreening_khong_kem_passScorePct_thi_OK_va_pass_score_null()
    {
        using var tdb = new CampaignTestDb();
        var org = Guid.NewGuid();
        var camp = CampaignTestDb.NewCampaign(org, CampaignStatus.Draft);
        camp.JDText = "JD";
        tdb.Db.Campaigns.Add(camp);
        tdb.Db.SaveChanges();

        var ctrl = Controller(tdb, org);
        var r = await CreatePolicy(ctrl, camp.Id, "CvScreening", "100 * strong_count / need_count", pass: null);
        Assert.Null(r.PassScorePct);
    }

    // ── seed: 2 mẫu CvScreening KHÔNG còn ngưỡng; 3 mẫu Interview vẫn 60 ──────────────────────
    [Fact]
    public async Task Seed_CvScreening_khong_co_nguong_Interview_van_60()
    {
        using var tdb = new CampaignTestDb();
        var seeds = await tdb.NewContext().ScoringPolicies
            .Where(p => p.CampaignId == null).ToListAsync();

        Assert.All(seeds.Where(p => p.Kind == ScoringExpressionKind.CvScreening),
            p => Assert.Null(p.PassScorePct));
        Assert.All(seeds.Where(p => p.Kind == ScoringExpressionKind.Interview),
            p => Assert.Equal(60, p.PassScorePct));
    }
}
