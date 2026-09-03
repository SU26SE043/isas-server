using System.Security.Claims;
using System.Text.Json;
using Isas.CampaignService.Controllers;
using Isas.CampaignService.DTOs;
using Isas.CampaignService.Models;
using Isas.CampaignService.Services;
using Isas.CampaignService.Validation;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging;
using Moq;
using Xunit;

using CampaignSvc = Isas.CampaignService.Services.CampaignService;

namespace Isas.CampaignService.Tests;

/// <summary>
/// RNK1 · HĐ-7 — ràng buộc CHÉO adaptive B2B: trần buổi <c>T = maxQuestions</c> phải đủ cho MỌI chuỗi
/// đào sâu tối đa <c>K × (1 + d)</c> (K = <c>questionsPerSession ?? số câu campaign</c>, d =
/// <c>maxDeepPerQuestion</c>). Lệch ⇒ <b>400</b> body
/// <c>{ code:"ADAPTIVE_BUDGET_TOO_SMALL", need, have, questions, deep }</c> ở create / update / publish.
/// <c>d &gt; 0</c> ⇒ <c>MaxFollowUps</c> ép về 0 (BUS-03: trần theo BUỔI không được bó chặt hơn trần theo CÂU).
/// </summary>
public class AdaptiveBudgetRnk1B6Tests
{
    private static IEntitlementClient Entitlements()
    {
        var m = new Mock<IEntitlementClient>();
        m.Setup(x => x.ResolveOrgAsync(It.IsAny<Guid>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(new CampaignEntitlement("test", "business", 5, 10, 200, true, true, true));
        return m.Object;
    }

    private static CampaignSvc NewService(CampaignDbContext db) =>
        new(db, Mock.Of<IFileService>(), Mock.Of<ILogger<CampaignSvc>>(), Mock.Of<IParserService>(),
            Mock.Of<ICriteriaSuggester>(), Mock.Of<IInvitationEmailPublisher>(), entitlements: Entitlements());

    private static CreateCampaignRequest BaseCreate(int questionCount = 0) => new()
    {
        Title = "Adaptive campaign",
        Domain = "BE",
        TimeLimitMinutes = 30,
        StartsAt = DateTime.UtcNow.AddMinutes(5),
        ExpiresAt = DateTime.UtcNow.AddDays(2),
        AdaptiveEnabled = true,
        Questions = Enumerable.Range(0, questionCount)
            .Select(i => new QuestionItem { QuestionText = $"Q{i}", IsRequired = true }).ToList(),
    };

    // ── (thuần) AdaptiveBudgetRule.Check ────────────────────────────────────────────────────────
    [Theory]
    [InlineData(5, 3, 20, false)]   // 20 = 5×4 → khít, hợp lệ
    [InlineData(5, 3, 19, true)]    // 19 < 20 → vi phạm
    [InlineData(5, 3, 0, false)]    // T = 0 → không có trần buổi → không ràng buộc
    [InlineData(5, 0, 10, false)]   // d = 0 → không phải chế độ chuỗi
    [InlineData(10, 3, 40, false)]  // 40 = 10×4 → khít
    [InlineData(10, 3, 39, true)]   // 39 < 40 → vi phạm
    [InlineData(0, 3, 5, false)]    // K = 0 (campaign chưa có câu) → need 0, không bao giờ vi phạm
    public void Check_BangGiaTri(int k, int d, int t, bool violated)
    {
        var v = AdaptiveBudgetRule.Check(k, d, t);
        if (!violated) { Assert.Null(v); return; }

        Assert.NotNull(v);
        Assert.Equal(k * (1 + d), v!.Need);
        Assert.Equal(t, v.Have);
        Assert.Equal(k, v.Questions);
        Assert.Equal(d, v.Deep);
    }

    // ── create: K=5, d=3, T=20 ⇒ 200 (khít) ────────────────────────────────────────────────────
    [Fact]
    public async Task Create_NganSachKhit_ChoQua()
    {
        using var tdb = new CampaignTestDb();
        var org = Guid.NewGuid();
        var req = BaseCreate();
        req.QuestionsPerSession = 5;
        req.MaxDeepPerQuestion = 3;
        req.MaxQuestions = 20;

        var res = await NewService(tdb.NewContext()).CreateCampaignAsync(org, org, req, default);

        Assert.Equal(20, res.MaxQuestions);
        Assert.Equal(3, res.MaxDeepPerQuestion);
    }

    // ── create: K=5, d=3, T=19 ⇒ AdaptiveBudgetTooSmallException, body đúng 4 số + code ──────────
    [Fact]
    public async Task Create_NganSachThieu_Nem_BodyDung4So()
    {
        using var tdb = new CampaignTestDb();
        var org = Guid.NewGuid();
        var req = BaseCreate();
        req.QuestionsPerSession = 5;
        req.MaxDeepPerQuestion = 3;
        req.MaxQuestions = 19;

        var ex = await Assert.ThrowsAsync<AdaptiveBudgetTooSmallException>(() =>
            NewService(tdb.NewContext()).CreateCampaignAsync(org, org, req, default));

        var body = JsonSerializer.SerializeToElement(ex.Body);
        Assert.Equal("ADAPTIVE_BUDGET_TOO_SMALL", body.GetProperty("code").GetString());
        Assert.Equal(20, body.GetProperty("need").GetInt32());
        Assert.Equal(19, body.GetProperty("have").GetInt32());
        Assert.Equal(5, body.GetProperty("questions").GetInt32());
        Assert.Equal(3, body.GetProperty("deep").GetInt32());

        Assert.Empty(await tdb.NewContext().Campaigns.ToListAsync());   // không để lại campaign nửa vời
    }

    // ── create: T = 0 (không trần buổi) ⇒ 200 dù d > 0 ─────────────────────────────────────────
    [Fact]
    public async Task Create_TranBuoi0_ChoQua()
    {
        using var tdb = new CampaignTestDb();
        var org = Guid.NewGuid();
        var req = BaseCreate();
        req.QuestionsPerSession = 5;
        req.MaxDeepPerQuestion = 3;
        // req.MaxQuestions = null ⇒ T = 0 ⇒ không ràng buộc

        var res = await NewService(tdb.NewContext()).CreateCampaignAsync(org, org, req, default);
        Assert.Null(res.MaxQuestions);
    }

    // ── create: d = 0 (không phải chế độ chuỗi) ⇒ 200 dù T nhỏ ─────────────────────────────────
    [Fact]
    public async Task Create_KhongPhaiCheDoChuoi_ChoQua()
    {
        using var tdb = new CampaignTestDb();
        var org = Guid.NewGuid();
        var req = BaseCreate();
        req.QuestionsPerSession = 5;
        req.MaxDeepPerQuestion = 0;
        req.MaxQuestions = 3;

        var res = await NewService(tdb.NewContext()).CreateCampaignAsync(org, org, req, default);
        Assert.Equal(3, res.MaxQuestions);
    }

    // ── BUS-03: d > 0 ⇒ campaign.MaxFollowUps ép về 0 dù request gửi 3 ──────────────────────────
    [Fact]
    public async Task Create_CheDoChuoi_EpMaxFollowUps0()
    {
        using var tdb = new CampaignTestDb();
        var org = Guid.NewGuid();
        var req = BaseCreate();
        req.QuestionsPerSession = 5;
        req.MaxDeepPerQuestion = 3;
        req.MaxQuestions = 20;
        req.MaxFollowUps = 3;   // HR gõ trần theo BUỔI

        var res = await NewService(tdb.NewContext()).CreateCampaignAsync(org, org, req, default);

        Assert.Equal(0, res.MaxFollowUps);
        Assert.Equal(0, (await tdb.NewContext().Campaigns.AsNoTracking().SingleAsync(c => c.Id == res.Id)).MaxFollowUps);
    }

    // ── update: chạm maxQuestions=19 trên campaign đang có qps=5, d=3 ⇒ 400 ─────────────────────
    [Fact]
    public async Task Update_ChamMaxQuestions_NganSachThieu_Nem()
    {
        using var tdb = new CampaignTestDb();
        var org = Guid.NewGuid();
        var camp = CampaignTestDb.NewCampaign(org);
        camp.AdaptiveEnabled = true;
        camp.QuestionsPerSession = 5;
        camp.MaxDeepPerQuestion = 3;
        camp.MaxQuestions = 20;
        tdb.Db.Campaigns.Add(camp);
        await tdb.Db.SaveChangesAsync();

        var ex = await Assert.ThrowsAsync<AdaptiveBudgetTooSmallException>(() =>
            NewService(tdb.NewContext()).UpdateCampaignAsync(org, org, camp.Id,
                new UpdateCampaignRequest { MaxQuestions = 19 }, default));

        var body = JsonSerializer.SerializeToElement(ex.Body);
        Assert.Equal(20, body.GetProperty("need").GetInt32());
        Assert.Equal(19, body.GetProperty("have").GetInt32());
    }

    // ── update: PUT KHÔNG chạm 3 số adaptive ⇒ KHÔNG kiểm (dù campaign hiện tại lệch) ──────────
    [Fact]
    public async Task Update_KhongChamAdaptive_KhongKiem()
    {
        using var tdb = new CampaignTestDb();
        var org = Guid.NewGuid();
        var camp = CampaignTestDb.NewCampaign(org);
        camp.QuestionsPerSession = 5;
        camp.MaxDeepPerQuestion = 3;
        camp.MaxQuestions = 19;   // đã lệch từ trước (đặt qua SQL / trước ràng buộc này)
        tdb.Db.Campaigns.Add(camp);
        await tdb.Db.SaveChangesAsync();

        var res = await NewService(tdb.NewContext()).UpdateCampaignAsync(org, org, camp.Id,
            new UpdateCampaignRequest { Title = "Chỉ đổi tên" }, default);

        Assert.Equal("Chỉ đổi tên", res.Title);
    }

    // ── publish CỨNG: campaign lệch (qps=5, d=3, T=19) ⇒ 400 ───────────────────────────────────
    [Fact]
    public async Task Publish_CampaignLech_Nem()
    {
        using var tdb = new CampaignTestDb();
        var org = Guid.NewGuid();
        var camp = CampaignTestDb.NewCampaign(org, CampaignStatus.Draft);
        camp.AdaptiveEnabled = true;
        camp.QuestionsPerSession = 5;
        camp.MaxDeepPerQuestion = 3;
        camp.MaxQuestions = 19;
        camp.Questions.Add(new CampaignQuestion
        {
            Id = Guid.NewGuid(), CampaignId = camp.Id, OrgId = org, QuestionText = "Q1",
            Source = QuestionSource.CustomHr, IsRequired = true, CreatedAt = DateTime.UtcNow,
        });
        camp.Criteria.Add(new CampaignCriterion
        {
            Id = Guid.NewGuid(), CampaignId = camp.Id, OrderNo = 0, Name = "A", Weight = 1.0m,
            MaxScore = 5, Source = CriterionSource.HrEdited, CreatedAt = DateTime.UtcNow, UpdatedAt = DateTime.UtcNow,
        });
        tdb.Db.Campaigns.Add(camp);
        await tdb.Db.SaveChangesAsync();

        var ex = await Assert.ThrowsAsync<AdaptiveBudgetTooSmallException>(() =>
            NewService(tdb.NewContext()).PublishCampaignAsync(org, org, camp.Id, default));

        var body = JsonSerializer.SerializeToElement(ex.Body);
        Assert.Equal(20, body.GetProperty("need").GetInt32());
        Assert.Equal(19, body.GetProperty("have").GetInt32());
        Assert.Equal(5, body.GetProperty("questions").GetInt32());
        Assert.Equal(3, body.GetProperty("deep").GetInt32());

        Assert.Equal(CampaignStatus.Draft,
            (await tdb.NewContext().Campaigns.AsNoTracking().SingleAsync(c => c.Id == camp.Id)).Status);
    }

    // ── publish: K từ SỐ CÂU CAMPAIGN (không có questionsPerSession) — 5 câu, d=3, T=19 ⇒ 400 ────
    [Fact]
    public async Task Publish_KTuSoCauCampaign_Lech_Nem()
    {
        using var tdb = new CampaignTestDb();
        var org = Guid.NewGuid();
        var camp = CampaignTestDb.NewCampaign(org, CampaignStatus.Draft);
        camp.AdaptiveEnabled = true;
        camp.MaxDeepPerQuestion = 3;
        camp.MaxQuestions = 19;
        for (var i = 0; i < 5; i++)
            camp.Questions.Add(new CampaignQuestion
            {
                Id = Guid.NewGuid(), CampaignId = camp.Id, OrgId = org, QuestionText = $"Q{i}",
                Source = QuestionSource.CustomHr, IsRequired = true, CreatedAt = DateTime.UtcNow,
            });
        camp.Criteria.Add(new CampaignCriterion
        {
            Id = Guid.NewGuid(), CampaignId = camp.Id, OrderNo = 0, Name = "A", Weight = 1.0m,
            MaxScore = 5, Source = CriterionSource.HrEdited, CreatedAt = DateTime.UtcNow, UpdatedAt = DateTime.UtcNow,
        });
        tdb.Db.Campaigns.Add(camp);
        await tdb.Db.SaveChangesAsync();

        var ex = await Assert.ThrowsAsync<AdaptiveBudgetTooSmallException>(() =>
            NewService(tdb.NewContext()).PublishCampaignAsync(org, org, camp.Id, default));

        Assert.Equal(5, JsonSerializer.SerializeToElement(ex.Body).GetProperty("questions").GetInt32());
    }

    // ── controller: 400 body STRUCTURED (không toast mã chung) ─────────────────────────────────
    [Fact]
    public async Task Controller_TraBodyStructured_KhongToastMaChung()
    {
        using var tdb = new CampaignTestDb();
        var org = Guid.NewGuid();
        var controller = new CampaignController(
            NewService(tdb.NewContext()), Mock.Of<ICvScreeningService>(),
            Mock.Of<ILogger<CampaignController>>());
        controller.ControllerContext = new ControllerContext
        {
            HttpContext = new DefaultHttpContext
            {
                User = new ClaimsPrincipal(new ClaimsIdentity(new[]
                {
                    new Claim(ClaimTypes.NameIdentifier, Guid.NewGuid().ToString()),
                    new Claim("org_id", org.ToString()),
                    new Claim("org_role", "OrgAdmin"),
                }, "Test")),
            },
        };

        var req = BaseCreate(questionCount: 1);
        req.QuestionsPerSession = 5;
        req.MaxDeepPerQuestion = 3;
        req.MaxQuestions = 19;

        var result = await controller.CreateCampaign(req, default);

        var bad = Assert.IsType<BadRequestObjectResult>(result.Result);
        var body = JsonSerializer.SerializeToElement(bad.Value!);
        Assert.Equal("ADAPTIVE_BUDGET_TOO_SMALL", body.GetProperty("code").GetString());
        Assert.Equal(20, body.GetProperty("need").GetInt32());
        Assert.Equal(19, body.GetProperty("have").GetInt32());
        Assert.Equal(5, body.GetProperty("questions").GetInt32());
        Assert.Equal(3, body.GetProperty("deep").GetInt32());
    }
}
