using Isas.CampaignService.Controllers;
using Isas.CampaignService.DTOs;
using Isas.CampaignService.Models;
using Isas.CampaignService.Services;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging;
using Moq;
using System.Reflection;
using System.Security.Claims;

using CampaignSvc = Isas.CampaignService.Services.CampaignService;

namespace Isas.CampaignService.Tests;

/// <summary>
/// F17 — Public API bên thứ ba: phạm vi ORG (key org A KHÔNG đọc được campaign org B), gating PII,
/// shape response hẹp (không rò overrideNote/flags), và ranh giới scheme (key ≠ JWT).
/// </summary>
public class PublicApiF17Tests
{
    private static CampaignSvc NewCampaignService(CampaignDbContext db) =>
        new(db, Mock.Of<IFileService>(), Mock.Of<ILogger<CampaignSvc>>(),
            Mock.Of<IParserService>(), Mock.Of<ICriteriaSuggester>(),
            Mock.Of<IInvitationEmailPublisher>());

    /// <summary>Controller với danh tính do scheme ApiKey cấp (mô phỏng handler đã chạy xong).</summary>
    private static PublicApiController NewController(CampaignDbContext db, Guid orgId, bool pii = false)
    {
        var controller = new PublicApiController(
            NewCampaignService(db), Mock.Of<ILogger<PublicApiController>>());

        var identity = new ClaimsIdentity(new[]
        {
            new Claim(ApiKeyDefaults.OrgIdClaim, orgId.ToString()),
            new Claim(ApiKeyDefaults.KeyIdClaim, Guid.NewGuid().ToString()),
            new Claim(ApiKeyDefaults.IncludePiiClaim, pii ? "true" : "false")
        }, ApiKeyDefaults.Scheme);

        controller.ControllerContext = new ControllerContext
        {
            HttpContext = new DefaultHttpContext { User = new ClaimsPrincipal(identity) }
        };
        return controller;
    }

    private static Campaign SeedCampaignWithResult(
        CampaignDbContext db, Guid orgId, decimal score = 80m, int? pass = 50)
    {
        var c = CampaignTestDb.NewCampaign(orgId, CampaignStatus.Active);
        c.PassScorePct = pass;
        db.Campaigns.Add(c);

        var candidateId = Guid.NewGuid();
        db.CampaignRankings.Add(new CampaignRanking
        {
            Id = Guid.NewGuid(),
            CampaignId = c.Id,
            CandidateId = candidateId,
            SessionId = Guid.NewGuid(),
            TotalScore = score,
            UpdatedAt = DateTime.UtcNow
        });
        // F5 — danh tính ứng viên (nguồn PII mà key phải được phép mới đọc được).
        var m = CampaignTestDb.NewMembership(c.Id, candidateId);
        m.FullName = "Nguyen Van A";
        m.Email = "a@example.com";
        db.CampaignMemberships.Add(m);
        db.SaveChanges();
        return c;
    }

    // ── Phạm vi ORG — ca quan trọng nhất của task ────────────────────────

    [Fact]
    public async Task Key_org_A_KHONG_doc_duoc_campaign_org_B()
    {
        using var tdb = new CampaignTestDb();
        var orgA = Guid.NewGuid();
        var orgB = Guid.NewGuid();
        var campaignB = SeedCampaignWithResult(tdb.Db, orgB);

        var result = await NewController(tdb.NewContext(), orgA)
            .GetCampaignResults(campaignB.Id, default);

        // 404 chứ không 403: không xác nhận hộ "campaign này có tồn tại" cho key org khác.
        Assert.IsType<NotFoundResult>(result.Result);
    }

    [Fact]
    public async Task Key_org_A_chi_thay_campaign_cua_org_A_trong_danh_sach()
    {
        using var tdb = new CampaignTestDb();
        var orgA = Guid.NewGuid();
        var orgB = Guid.NewGuid();
        var campaignA = SeedCampaignWithResult(tdb.Db, orgA);
        SeedCampaignWithResult(tdb.Db, orgB);

        var result = await NewController(tdb.NewContext(), orgA).ListCampaigns();

        var items = Assert.IsType<List<PublicCampaignSummary>>(
            Assert.IsType<OkObjectResult>(result.Result).Value);
        var only = Assert.Single(items);
        Assert.Equal(campaignA.Id, only.Id);
    }

    [Fact]
    public async Task Key_doc_duoc_campaign_cua_chinh_org_minh()
    {
        using var tdb = new CampaignTestDb();
        var orgId = Guid.NewGuid();
        var campaign = SeedCampaignWithResult(tdb.Db, orgId, score: 80m, pass: 50);

        var result = await NewController(tdb.NewContext(), orgId)
            .GetCampaignResults(campaign.Id, default);

        var body = Assert.IsType<PublicCampaignResultsResponse>(
            Assert.IsType<OkObjectResult>(result.Result).Value);
        Assert.Equal(campaign.Id, body.CampaignId);
        var row = Assert.Single(body.Results);
        Assert.Equal(80m, row.TotalScore);
        Assert.Equal("Pass", row.Result);
        Assert.Equal(1, row.Rank);
    }

    [Fact]
    public async Task Thieu_claim_org_thi_403_khong_tra_du_lieu()
    {
        using var tdb = new CampaignTestDb();
        var campaign = SeedCampaignWithResult(tdb.Db, Guid.NewGuid());

        var controller = new PublicApiController(
            NewCampaignService(tdb.NewContext()), Mock.Of<ILogger<PublicApiController>>());
        controller.ControllerContext = new ControllerContext
        {
            // Danh tính KHÔNG có claim org (fail-closed).
            HttpContext = new DefaultHttpContext
            {
                User = new ClaimsPrincipal(new ClaimsIdentity(Array.Empty<Claim>(), ApiKeyDefaults.Scheme))
            }
        };

        Assert.IsType<ForbidResult>((await controller.GetCampaignResults(campaign.Id, default)).Result);
        Assert.IsType<ForbidResult>((await controller.ListCampaigns()).Result);
    }

    // ── Gating PII ───────────────────────────────────────────────────────

    [Fact]
    public async Task Key_khong_bat_pii_thi_khong_tra_ten_email()
    {
        using var tdb = new CampaignTestDb();
        var orgId = Guid.NewGuid();
        var campaign = SeedCampaignWithResult(tdb.Db, orgId);

        var result = await NewController(tdb.NewContext(), orgId, pii: false)
            .GetCampaignResults(campaign.Id, default);

        var body = Assert.IsType<PublicCampaignResultsResponse>(
            Assert.IsType<OkObjectResult>(result.Result).Value);
        Assert.False(body.PiiIncluded);
        var row = Assert.Single(body.Results);
        Assert.Null(row.FullName);
        Assert.Null(row.Email);
        Assert.NotEqual(Guid.Empty, row.CandidateId);   // vẫn đối chiếu được bằng id
    }

    [Fact]
    public async Task Key_bat_pii_thi_tra_ten_email()
    {
        using var tdb = new CampaignTestDb();
        var orgId = Guid.NewGuid();
        var campaign = SeedCampaignWithResult(tdb.Db, orgId);

        var result = await NewController(tdb.NewContext(), orgId, pii: true)
            .GetCampaignResults(campaign.Id, default);

        var body = Assert.IsType<PublicCampaignResultsResponse>(
            Assert.IsType<OkObjectResult>(result.Result).Value);
        Assert.True(body.PiiIncluded);
        var row = Assert.Single(body.Results);
        Assert.Equal("Nguyen Van A", row.FullName);
        Assert.Equal("a@example.com", row.Email);
    }

    // ── Shape response hẹp ───────────────────────────────────────────────

    [Fact]
    public void DTO_public_KHONG_co_ghi_chu_HR_va_co_chong_gian_lan()
    {
        var props = typeof(PublicCampaignResultRow).GetProperties().Select(p => p.Name).ToList();

        // OverrideNote = ghi chú riêng của HR; Flags = tín hiệu chống gian lận (CAMP-12/D13 — cờ để
        // HR tự đánh giá, đẩy sang ATS là mời auto-loại). Cả hai KHÔNG được có mặt trong hợp đồng public.
        Assert.DoesNotContain("OverrideNote", props);
        Assert.DoesNotContain("Flags", props);
        Assert.DoesNotContain("AiScore", props);
        Assert.DoesNotContain("OverrideScore", props);
    }

    [Fact]
    public async Task Override_cua_HR_chi_lo_co_HrReviewed_khong_lo_ghi_chu()
    {
        using var tdb = new CampaignTestDb();
        var orgId = Guid.NewGuid();
        var campaign = SeedCampaignWithResult(tdb.Db, orgId, score: 40m, pass: 50);

        // Sentinel ASCII CÓ CHỦ Ý: System.Text.Json escape ký tự ngoài ASCII (í…), nên nếu note
        // có dấu tiếng Việt thì DoesNotContain(note, json) sẽ XANH kể cả khi note ĐÃ rò ra payload —
        // assert vô hiệu trong im lặng. Phát hiện qua mutation-check M9 (thêm OverrideNote vào DTO
        // public: test phản chiếu đỏ, test này vẫn xanh) → đổi sang sentinel ASCII để so khớp thật.
        const string secretNote = "INTERNAL-HR-ONLY-SENTINEL";
        var ranking = await tdb.NewContext().CampaignRankings.SingleAsync();
        await NewCampaignService(tdb.NewContext()).OverrideResultAsync(
            orgId, Guid.NewGuid(), campaign.Id, ranking.SessionId,
            new OverrideResultRequest { Score = 90m, Result = "Pass", Note = secretNote }, default);

        var result = await NewController(tdb.NewContext(), orgId)
            .GetCampaignResults(campaign.Id, default);

        var body = Assert.IsType<PublicCampaignResultsResponse>(
            Assert.IsType<OkObjectResult>(result.Result).Value);
        var row = Assert.Single(body.Results);
        Assert.True(row.HrReviewed);
        Assert.Equal(90m, row.TotalScore);     // điểm effective
        Assert.Equal("Pass", row.Result);
        // Ghi chú nội bộ không được xuất hiện ở BẤT KỲ đâu trong payload public (quét cả cây JSON,
        // không chỉ các field ta nhớ ra để assert riêng).
        Assert.DoesNotContain(secretNote, System.Text.Json.JsonSerializer.Serialize(body));
    }

    // ── Ranh giới scheme: key ≠ JWT ──────────────────────────────────────

    [Fact]
    public void PublicApi_chi_nhan_scheme_ApiKey_KHONG_nhan_JWT()
    {
        var attr = typeof(PublicApiController).GetCustomAttribute<AuthorizeAttribute>();

        Assert.NotNull(attr);
        // Khai tường minh scheme ⇒ Bearer JWT không xác thực được endpoint này.
        Assert.Equal(ApiKeyDefaults.Scheme, attr!.AuthenticationSchemes);
    }

    [Fact]
    public void ApiKeysController_quan_ly_key_dung_JWT_Employer_KHONG_dung_api_key()
    {
        var attr = typeof(ApiKeysController).GetCustomAttribute<AuthorizeAttribute>();

        Assert.NotNull(attr);
        Assert.Equal("Employer", attr!.Roles);
        // Không khai scheme ⇒ dùng scheme mặc định (Bearer JWT) ⇒ X-Api-Key KHÔNG mở được
        // màn hình quản lý key (nếu mở được thì một key rò rỉ sẽ tự cấp thêm key cho chính nó).
        Assert.True(string.IsNullOrEmpty(attr.AuthenticationSchemes));
    }
}
