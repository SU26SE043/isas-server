using System.Security.Claims;
using Isas.CampaignService.Controllers;
using Isas.CampaignService.DTOs;
using Isas.CampaignService.Models;
using Isas.CampaignService.Services;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging;
using Moq;
using Xunit;

using CampaignSvc = Isas.CampaignService.Services.CampaignService;

namespace Isas.CampaignService.Tests;

/// <summary>
/// CMP-B1 — ràng buộc "chiến dịch phải có ≥1 câu hỏi" đã CHUYỂN từ lúc TẠO sang lúc XUẤT BẢN.
///
/// <para>Bối cảnh: FE dựng bản nháp TRƯỚC rồi mới gọi endpoint sinh câu hỏi bằng AI, nên nháp lúc
/// <c>POST /api/v1/campaign</c> chưa có câu nào. Chốt cũ ở <see cref="CampaignController.CreateCampaign"/>
/// làm <c>POST /campaign</c> trả 400 ⇒ "sinh câu hỏi bằng AI cho chiến dịch mới" bế tắc.</para>
///
/// <para>Bất biến vẫn giữ: <see cref="CampaignService.PublishCampaignAsync"/> ném
/// <see cref="InvalidOperationException"/> ("Campaign phải có ≥1 câu hỏi trước khi publish.") →
/// <see cref="CampaignController.PublishCampaign"/> → <b>409 Conflict</b>. Chiến dịch rỗng KHÔNG lên sóng.</para>
///
/// <para>Chốt bị gỡ NẰM Ở CONTROLLER ⇒ 4 test đầu chạm lớp controller trực tiếp (test tầng service
/// sẽ xanh dù chưa gỡ gì).</para>
/// </summary>
public class CampaignQuestionsAtPublishCmpB1Tests
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

    private static CampaignController NewController(CampaignDbContext db, Guid orgId)
    {
        var c = new CampaignController(
            NewService(db), Mock.Of<ICvScreeningService>(), Mock.Of<ILogger<CampaignController>>());
        c.ControllerContext = new ControllerContext
        {
            HttpContext = new DefaultHttpContext
            {
                User = new ClaimsPrincipal(new ClaimsIdentity(new[]
                {
                    new Claim(ClaimTypes.NameIdentifier, Guid.NewGuid().ToString()),
                    new Claim("org_id", orgId.ToString()),
                    new Claim("org_role", "OrgAdmin"),
                }, "Test")),
            },
        };
        return c;
    }

    private static CreateCampaignRequest BaseCreate() => new()
    {
        Title = "CMP-B1 campaign",
        Domain = "BE",
        TimeLimitMinutes = 30,
        StartsAt = DateTime.UtcNow.AddMinutes(5),
        ExpiresAt = DateTime.UtcNow.AddDays(2),
        Questions = new List<QuestionItem>(),
    };

    private static CampaignQuestion Q(Guid campaignId, Guid orgId, string text = "Câu hỏi thật") => new()
    {
        Id = Guid.NewGuid(), CampaignId = campaignId, OrgId = orgId, QuestionText = text,
        Source = QuestionSource.CustomHr, IsRequired = true, CreatedAt = DateTime.UtcNow,
    };

    private static CampaignCriterion Crit(Guid campaignId) => new()
    {
        Id = Guid.NewGuid(), CampaignId = campaignId, OrderNo = 0, Name = "A", Weight = 1.0m,
        MaxScore = 5, Source = CriterionSource.HrEdited, CreatedAt = DateTime.UtcNow, UpdatedAt = DateTime.UtcNow,
    };

    // ── 1. TẠO với questions: [] ⇒ KHÔNG bị chặn; chiến dịch tạo ra có 0 câu hỏi ────────────────
    [Fact]
    public async Task CreateCampaign_QuestionsRong_KhongBiChan_TaoDuocNhap0Cau()
    {
        using var tdb = new CampaignTestDb();
        var org = Guid.NewGuid();
        var controller = NewController(tdb.NewContext(), org);

        var result = await controller.CreateCampaign(BaseCreate(), default);   // questions = []

        var ok = Assert.IsType<OkObjectResult>(result.Result);   // controller trả Ok() (200), không CreatedAt
        var body = Assert.IsType<CampaignResponse>(ok.Value);
        Assert.Empty(body.Questions);
        Assert.Equal("Draft", body.Status);

        var saved = await tdb.NewContext().Campaigns
            .Include(c => c.Questions)
            .SingleAsync(c => c.Id == body.Id);
        Assert.Empty(saved.Questions);
    }

    // ── 2. Chốt ":143" vẫn sống: câu nào CÓ thì không được rỗng chữ ────────────────────────────
    [Fact]
    public async Task CreateCampaign_CoCauRongChu_400()
    {
        using var tdb = new CampaignTestDb();
        var org = Guid.NewGuid();
        var controller = NewController(tdb.NewContext(), org);

        var req = BaseCreate();
        req.Questions = new List<QuestionItem> { new() { QuestionText = "  " } };

        var result = await controller.CreateCampaign(req, default);

        var bad = Assert.IsType<BadRequestObjectResult>(result.Result);
        Assert.Contains("non-empty text", bad.Value!.ToString());
        Assert.Empty(await tdb.NewContext().Campaigns.ToListAsync());
    }

    // ── 3. Chốt UpdateQuestions vẫn sống: PUT /questions với [] ⇒ 400 ──────────────────────────
    //    (endpoint THAY TOÀN BỘ danh sách — mảng rỗng = xoá sạch câu hỏi, khác việc tạo nháp)
    [Fact]
    public async Task UpdateCampaignQuestions_MangRong_400()
    {
        using var tdb = new CampaignTestDb();
        var controller = NewController(tdb.NewContext(), Guid.NewGuid());

        var result = await controller.UpdateCampaignQuestions(
            Guid.NewGuid(), new List<QuestionItem>(), default);

        var bad = Assert.IsType<BadRequestObjectResult>(result.Result);
        Assert.Contains("At least one question is required", bad.Value!.ToString());
    }

    // ── 4. PUBLISH chiến dịch 0 câu ⇒ 409, thông điệp chứa "≥1 câu hỏi" ────────────────────────
    [Fact]
    public async Task Publish_0Cau_409_ThongDiepCo1CauHoi()
    {
        using var tdb = new CampaignTestDb();
        var org = Guid.NewGuid();
        var camp = CampaignTestDb.NewCampaign(org, CampaignStatus.Draft);
        camp.Criteria.Add(Crit(camp.Id));   // có tiêu chí sẵn — để chắc chốt dừng ĐÚNG ở guard câu hỏi
        tdb.Db.Campaigns.Add(camp);
        await tdb.Db.SaveChangesAsync();

        var controller = NewController(tdb.NewContext(), org);
        var result = await controller.PublishCampaign(camp.Id, default);

        var conflict = Assert.IsType<ConflictObjectResult>(result.Result);
        Assert.Contains("≥1 câu hỏi", conflict.Value!.ToString());

        Assert.Equal(CampaignStatus.Draft,
            (await tdb.NewContext().Campaigns.AsNoTracking().SingleAsync(c => c.Id == camp.Id)).Status);
    }

    // ── 5. PUBLISH chiến dịch có ≥1 câu hỏi ⇒ KHÔNG bị chặn vì lý do câu hỏi ───────────────────
    [Fact]
    public async Task Publish_Co1Cau_KhongBiChanViCauHoi()
    {
        using var tdb = new CampaignTestDb();
        var org = Guid.NewGuid();
        var camp = CampaignTestDb.NewCampaign(org, CampaignStatus.Draft);
        camp.Questions.Add(Q(camp.Id, org));
        camp.Criteria.Add(Crit(camp.Id));
        tdb.Db.Campaigns.Add(camp);
        await tdb.Db.SaveChangesAsync();

        var controller = NewController(tdb.NewContext(), org);
        var result = await controller.PublishCampaign(camp.Id, default);

        var ok = Assert.IsType<OkObjectResult>(result.Result);
        var body = Assert.IsType<CampaignResponse>(ok.Value);
        Assert.Equal("Active", body.Status);   // publish đi qua trót lọt — guard câu hỏi không cản

        Assert.Equal(CampaignStatus.Active,
            (await tdb.NewContext().Campaigns.AsNoTracking().SingleAsync(c => c.Id == camp.Id)).Status);
    }
}
