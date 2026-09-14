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

namespace Isas.CampaignService.Tests;

/// <summary>
/// SCP1-B3 — POST /campaign/{id}/scoring-policies/validate (HĐ-2). Thuần kiểm tra: bộ MẪU cố định
/// trong code, KHÔNG đọc dữ liệu ứng viên, KHÔNG ghi DB. Lỗi = MÃ + [start,end).
/// </summary>
public class ScoringPolicyValidateTests
{
    private static (CampaignController Controller, CampaignTestDb Db, Guid OrgId, Guid CampaignId) Setup(
        bool withOrgClaim = true)
    {
        var tdb = new CampaignTestDb();
        var orgId = Guid.NewGuid();
        var campaign = CampaignTestDb.NewCampaign(orgId);
        tdb.Db.Campaigns.Add(campaign);
        tdb.Db.SaveChanges();

        var controller = new CampaignController(
            Mock.Of<ICampaignService>(),
            Mock.Of<ICvScreeningService>(),
            Mock.Of<ILogger<CampaignController>>(),
            policies: new ScoringPolicyService(tdb.NewContext()));

        var claims = new List<Claim> { new(ClaimTypes.NameIdentifier, Guid.NewGuid().ToString()) };
        if (withOrgClaim) claims.Add(new Claim("org_id", orgId.ToString()));
        controller.ControllerContext = new ControllerContext
        {
            HttpContext = new DefaultHttpContext { User = new ClaimsPrincipal(new ClaimsIdentity(claims, "Test")) }
        };

        return (controller, tdb, orgId, campaign.Id);
    }

    private static async Task<ScoringPolicyValidateResponse> Ok(
        CampaignController c, Guid campaignId, string kind, string? expr)
    {
        var action = await c.ValidateScoringPolicy(campaignId,
            new ScoringPolicyValidateRequest { Kind = kind, Expression = expr }, default);
        var ok = Assert.IsType<OkObjectResult>(action.Result);
        return Assert.IsType<ScoringPolicyValidateResponse>(ok.Value);
    }

    // ── valid: true ────────────────────────────────────────────────────────────────────────────
    [Fact]
    public async Task Bieu_thuc_hop_le_tra_valid_true_va_sampleScore()
    {
        var (c, db, _, cid) = Setup();
        using var _d = db;

        var r = await Ok(c, cid, "Interview", "weighted_avg_pct");

        Assert.True(r.Valid);
        Assert.Equal(66m, r.SampleScore);   // bộ mẫu Interview: pct 80(.5) 60(.3) 40(.2)
        Assert.Null(r.Errors);
    }

    [Theory]
    [InlineData("Interview", "weighted_avg_pct * completeness", "52.8")]       // 66 * 0.8
    [InlineData("Interview", "if(min_pct < 40, min_pct, weighted_avg_pct)", "66")]
    [InlineData("CvScreening", "round(100 * (strong_count + 0.5 * partial_count) / need_count)", "67")]
    public async Task Bieu_thuc_hop_le_khac(string kind, string expr, string expected)
    {
        var (c, db, _, cid) = Setup();
        using var _d = db;

        var r = await Ok(c, cid, kind, expr);

        Assert.True(r.Valid);
        Assert.Equal(decimal.Parse(expected, System.Globalization.CultureInfo.InvariantCulture), r.SampleScore);
    }

    [Fact]
    public async Task sampleScore_luon_trong_0_100()
    {
        var (c, db, _, cid) = Setup();
        using var _d = db;

        var r = await Ok(c, cid, "CvScreening", "100 * (strong_count + 0.5 * partial_count) / need_count");

        Assert.True(r.Valid);
        Assert.NotNull(r.SampleScore);
        Assert.InRange(r.SampleScore!.Value, 0m, 100m);
    }

    // ── valid: false + { code, start, end } ────────────────────────────────────────────────────
    [Fact]
    public async Task Bien_khong_ton_tai_tra_UNKNOWN_VARIABLE_dung_vi_tri()
    {
        var (c, db, _, cid) = Setup();
        using var _d = db;
        const string expr = "weighted_avg_pct * khong_ton_tai";

        var r = await Ok(c, cid, "Interview", expr);

        Assert.False(r.Valid);
        Assert.Null(r.SampleScore);
        var e = Assert.Single(r.Errors!);
        Assert.Equal("UNKNOWN_VARIABLE", e.Code);
        Assert.Equal("weighted_avg_pct * ".Length, e.Start);   // 19
        Assert.Equal(expr.Length, e.End);                       // 32
        Assert.Equal("khong_ton_tai", expr[e.Start..e.End]);    // khoảng ký tự chỉ đúng token
    }

    [Fact]
    public async Task Thieu_ve_phai_tra_SYNTAX_ERROR()
    {
        var (c, db, _, cid) = Setup();
        using var _d = db;

        var r = await Ok(c, cid, "Interview", "weighted_avg_pct * ");

        Assert.False(r.Valid);
        Assert.Equal("SYNTAX_ERROR", Assert.Single(r.Errors!).Code);
    }

    [Fact]
    public async Task Ket_qua_ngoai_0_100_tra_RESULT_OUT_OF_RANGE()
    {
        var (c, db, _, cid) = Setup();
        using var _d = db;

        var r = await Ok(c, cid, "Interview", "weighted_avg_pct + 100");

        Assert.False(r.Valid);
        Assert.Equal("RESULT_OUT_OF_RANGE", Assert.Single(r.Errors!).Code);
    }

    [Fact]
    public async Task Bieu_thuc_rong_tra_SYNTAX_ERROR_khong_500()
    {
        var (c, db, _, cid) = Setup();
        using var _d = db;

        var r = await Ok(c, cid, "Interview", "");

        Assert.False(r.Valid);
        Assert.Equal("SYNTAX_ERROR", Assert.Single(r.Errors!).Code);
    }

    [Fact]
    public async Task Bien_kiem_theo_dung_kind()
    {
        var (c, db, _, cid) = Setup();
        using var _d = db;

        // strong_count hợp lệ ở CvScreening, KHÔNG ở Interview.
        var iv = await Ok(c, cid, "Interview", "strong_count");
        Assert.False(iv.Valid);
        Assert.Equal("UNKNOWN_VARIABLE", Assert.Single(iv.Errors!).Code);

        var cv = await Ok(c, cid, "CvScreening", "strong_count");
        Assert.True(cv.Valid);            // strong_count = 3 (bộ mẫu CV) ∈ [0,100]
        Assert.Equal(3m, cv.SampleScore);
    }

    // ── phong bì request / quyền ──────────────────────────────────────────────────────────────
    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("interview")]     // sai hoa/thường
    [InlineData("0")]            // số enum
    [InlineData("Foo")]
    public async Task kind_sai_hoac_thieu_tra_400_khong_phai_ma_loi_bieu_thuc(string? kind)
    {
        var (c, db, _, cid) = Setup();
        using var _d = db;

        var action = await c.ValidateScoringPolicy(cid,
            new ScoringPolicyValidateRequest { Kind = kind, Expression = "weighted_avg_pct" }, default);

        Assert.IsType<BadRequestObjectResult>(action.Result);
    }

    [Fact]
    public async Task Campaign_ngoai_org_tra_404()
    {
        var (c, db, _, _) = Setup();
        using var _d = db;

        var action = await c.ValidateScoringPolicy(Guid.NewGuid(),   // campaign không thuộc org
            new ScoringPolicyValidateRequest { Kind = "Interview", Expression = "weighted_avg_pct" }, default);

        Assert.IsType<NotFoundResult>(action.Result);
    }

    [Fact]
    public async Task Thieu_claim_org_id_tra_403()
    {
        var (c, db, _, cid) = Setup(withOrgClaim: false);
        using var _d = db;

        var action = await c.ValidateScoringPolicy(cid,
            new ScoringPolicyValidateRequest { Kind = "Interview", Expression = "weighted_avg_pct" }, default);

        Assert.IsType<ForbidResult>(action.Result);
    }

    // ── không đọc dữ liệu ứng viên · không ghi DB ─────────────────────────────────────────────
    [Fact]
    public async Task Campaign_khong_co_ung_vien_van_tra_sampleScore()
    {
        var (c, db, _, cid) = Setup();
        using var _d = db;

        // campaign vừa tạo — 0 cv_submission, 0 ranking. sampleScore đến từ code, không phải DB.
        var r = await Ok(c, cid, "Interview", "weighted_avg_pct");
        Assert.True(r.Valid);
        Assert.Equal(66m, r.SampleScore);
    }

    // ── hình dạng trên dây khớp HĐ-2 (camelCase, valid ẩn errors / invalid ẩn sampleScore) ────
    [Fact]
    public async Task Wire_shape_khop_HĐ2()
    {
        var (c, db, _, cid) = Setup();
        using var _d = db;

        // Cùng cấu hình JSON với Program.cs AddJsonOptions (mặc định MVC = camelCase).
        var opts = new System.Text.Json.JsonSerializerOptions(System.Text.Json.JsonSerializerDefaults.Web);

        var okResp = await Ok(c, cid, "Interview", "weighted_avg_pct");
        var okJson = System.Text.Json.JsonSerializer.Serialize(okResp, opts);
        Assert.Equal("{\"valid\":true,\"sampleScore\":66}", okJson);   // KHÔNG có "errors"

        var badResp = await Ok(c, cid, "Interview", "weighted_avg_pct * khong_ton_tai");
        var badJson = System.Text.Json.JsonSerializer.Serialize(badResp, opts);
        Assert.Equal(
            "{\"valid\":false,\"errors\":[{\"code\":\"UNKNOWN_VARIABLE\",\"start\":19,\"end\":32}]}",
            badJson);   // KHÔNG có "sampleScore"
    }

    [Fact]
    public async Task Khong_ghi_gi_vao_DB()
    {
        var (c, db, orgId, cid) = Setup();
        using var _d = db;

        int policiesBefore = await db.Db.ScoringPolicies.CountAsync();
        int campaignsBefore = await db.Db.Campaigns.CountAsync();

        await Ok(c, cid, "Interview", "weighted_avg_pct");
        await Ok(c, cid, "Interview", "weighted_avg_pct * khong_ton_tai");
        await c.ValidateScoringPolicy(cid,
            new ScoringPolicyValidateRequest { Kind = "Foo", Expression = "x" }, default);

        using var check = db.NewContext();
        Assert.Equal(policiesBefore, await check.ScoringPolicies.CountAsync());
        Assert.Equal(campaignsBefore, await check.Campaigns.CountAsync());
        Assert.Equal(5, policiesBefore);   // vẫn đúng 5 mẫu seed
    }
}
