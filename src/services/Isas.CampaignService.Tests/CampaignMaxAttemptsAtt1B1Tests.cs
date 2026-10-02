using System.Security.Claims;
using System.Text.Json;
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
/// ATT1-B1 — số lượt làm bài tối đa (<c>campaigns.max_attempts</c>) + luật cho thời lượng
/// (<c>time_limit_minutes</c>). Hợp đồng ATT1 [C1]–[C5].
///
/// <para>Mọi khẳng định về mã lỗi / khoá JSON đi qua <b>controller</b> rồi SERIALIZE body bằng options
/// web (camelCase) — assert object C# không chứng minh được tên khoá mà FE đọc.</para>
/// </summary>
public class CampaignMaxAttemptsAtt1B1Tests
{
    private static readonly JsonSerializerOptions Web = new(JsonSerializerDefaults.Web);

    private static IEntitlementClient Entitlements()
    {
        var m = new Mock<IEntitlementClient>();
        m.Setup(x => x.ResolveOrgAsync(It.IsAny<Guid>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(new CampaignEntitlement("test", "business", 5, 10, 200, true, true, true));
        return m.Object;
    }

    private static CampaignSvc NewService(CampaignDbContext db, ICriteriaSuggester? suggester = null) =>
        new(db, Mock.Of<IFileService>(), Mock.Of<ILogger<CampaignSvc>>(), Mock.Of<IParserService>(),
            suggester ?? Mock.Of<ICriteriaSuggester>(), Mock.Of<IInvitationEmailPublisher>(),
            entitlements: Entitlements());

    private static CampaignController NewController(CampaignSvc svc, Guid orgId)
    {
        var c = new CampaignController(svc, Mock.Of<ICvScreeningService>(), Mock.Of<ILogger<CampaignController>>());
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

    private static CreateCampaignRequest CreateReq(int? maxAttempts = null, int? timeLimit = 30) => new()
    {
        Title = "ATT1",
        Domain = "BE",
        TimeLimitMinutes = timeLimit,
        MaxAttempts = maxAttempts,
        StartsAt = DateTime.UtcNow.AddMinutes(5),
        ExpiresAt = DateTime.UtcNow.AddDays(7),
        Questions = new List<QuestionItem>(),
    };

    private static Campaign Seed(CampaignDbContext db, Guid org, CampaignStatus status,
        int maxAttempts = 1, int? timeLimit = 30)
    {
        var c = CampaignTestDb.NewCampaign(org, status);
        c.Domain = "BE";
        c.MaxAttempts = maxAttempts;
        c.TimeLimitMinutes = timeLimit;
        db.Campaigns.Add(c);
        db.SaveChanges();
        return c;
    }

    // Body JSON THẬT (camelCase) của một ObjectResult.
    private static JsonElement Body(IActionResult? r)
    {
        var obj = Assert.IsAssignableFrom<ObjectResult>(r);
        return JsonSerializer.SerializeToElement(obj.Value, obj.Value!.GetType(), Web);
    }

    private static JsonElement Body<T>(ActionResult<T> r) => Body(r.Result);

    private static void AssertErrorBody(JsonElement body, string? expectedCode)
    {
        Assert.Equal(JsonValueKind.Object, body.ValueKind);
        Assert.False(string.IsNullOrWhiteSpace(body.GetProperty("error").GetString()));
        if (expectedCode is null)
            Assert.False(body.TryGetProperty("code", out _), "409 generic KHÔNG mang code");
        else
            Assert.Equal(expectedCode, body.GetProperty("code").GetString());
    }

    private static int StoredMaxAttempts(CampaignTestDb tdb, Guid id) =>
        tdb.NewContext().Campaigns.AsNoTracking().Single(c => c.Id == id).MaxAttempts;

    private static int? StoredTimeLimit(CampaignTestDb tdb, Guid id) =>
        tdb.NewContext().Campaigns.AsNoTracking().Single(c => c.Id == id).TimeLimitMinutes;

    // ── [C1] create ─────────────────────────────────────────────────────────────────────────────

    [Fact]
    public async Task Create_KhongGuiMaxAttempts_Bang1_TrongJsonVaDb()
    {
        using var tdb = new CampaignTestDb();
        var org = Guid.NewGuid();
        // JSON thật không có khoá maxAttempts ⇒ bind ra null ⇒ mặc định 1.
        var req = JsonSerializer.Deserialize<CreateCampaignRequest>(
            $$"""{"title":"ATT1","domain":"BE","timeLimitMinutes":30,"startsAt":"{{DateTime.UtcNow.AddMinutes(5):O}}","expiresAt":"{{DateTime.UtcNow.AddDays(7):O}}","questions":[]}""",
            Web)!;
        Assert.Null(req.MaxAttempts);

        var res = await NewController(NewService(tdb.NewContext()), org).CreateCampaign(req, default);

        var body = Body(res);
        Assert.Equal(1, body.GetProperty("maxAttempts").GetInt32());
        Assert.Equal(1, StoredMaxAttempts(tdb, body.GetProperty("id").GetGuid()));
    }

    [Theory]
    [InlineData(1)]
    [InlineData(3)]
    public async Task Create_MaxAttemptsBien_HopLe_LuuDungGiaTri(int n)
    {
        using var tdb = new CampaignTestDb();
        var org = Guid.NewGuid();

        var res = await NewService(tdb.NewContext()).CreateCampaignAsync(org, org, CreateReq(maxAttempts: n), default);

        Assert.Equal(n, res.MaxAttempts);
        Assert.Equal(n, StoredMaxAttempts(tdb, res.Id));
    }

    [Theory]
    [InlineData(0)]
    [InlineData(4)]
    [InlineData(-1)]
    public async Task Create_MaxAttemptsNgoaiDai_400_BodyError_KhongTaoGi(int n)
    {
        using var tdb = new CampaignTestDb();
        var org = Guid.NewGuid();

        var res = await NewController(NewService(tdb.NewContext()), org).CreateCampaign(CreateReq(maxAttempts: n), default);

        Assert.IsType<BadRequestObjectResult>(res.Result);
        AssertErrorBody(Body(res), expectedCode: null);
        Assert.Empty(await tdb.NewContext().Campaigns.ToListAsync());
    }

    [Theory]
    [InlineData(4)]
    [InlineData(181)]
    [InlineData(0)]
    [InlineData(100000)]
    public async Task Create_TimeLimitNgoaiDai_400_BodyError_KhongTaoGi(int minutes)
    {
        using var tdb = new CampaignTestDb();
        var org = Guid.NewGuid();

        var res = await NewController(NewService(tdb.NewContext()), org).CreateCampaign(CreateReq(timeLimit: minutes), default);

        Assert.IsType<BadRequestObjectResult>(res.Result);
        AssertErrorBody(Body(res), expectedCode: null);
        Assert.Empty(await tdb.NewContext().Campaigns.ToListAsync());
    }

    [Theory]
    [InlineData(5)]
    [InlineData(180)]
    public async Task Create_TimeLimitBien_HopLe(int minutes)
    {
        using var tdb = new CampaignTestDb();
        var org = Guid.NewGuid();

        var res = await NewService(tdb.NewContext()).CreateCampaignAsync(org, org, CreateReq(timeLimit: minutes), default);

        Assert.Equal(minutes, StoredTimeLimit(tdb, res.Id));
    }

    // ── update: dải giá trị (Draft) ─────────────────────────────────────────────────────────────

    [Theory]
    [InlineData(4)]
    [InlineData(181)]
    public async Task Update_Draft_TimeLimitNgoaiDai_400_BodyError_KhongLuu(int minutes)
    {
        using var tdb = new CampaignTestDb();
        var org = Guid.NewGuid();
        var camp = Seed(tdb.Db, org, CampaignStatus.Draft, timeLimit: 30);

        var res = await NewController(NewService(tdb.NewContext()), org).UpdateCampaign(
            camp.Id, new UpdateCampaignRequest { Title = camp.Title, TimeLimitMinutes = minutes }, default);

        Assert.IsType<BadRequestObjectResult>(res.Result);
        AssertErrorBody(Body(res), expectedCode: null);
        Assert.Equal(30, StoredTimeLimit(tdb, camp.Id));
    }

    [Theory]
    [InlineData(0)]
    [InlineData(4)]
    public async Task Update_Draft_MaxAttemptsNgoaiDai_400_BodyError_KhongLuu(int n)
    {
        using var tdb = new CampaignTestDb();
        var org = Guid.NewGuid();
        var camp = Seed(tdb.Db, org, CampaignStatus.Draft, maxAttempts: 2);

        var res = await NewController(NewService(tdb.NewContext()), org).UpdateCampaign(
            camp.Id, new UpdateCampaignRequest { Title = camp.Title, MaxAttempts = n }, default);

        Assert.IsType<BadRequestObjectResult>(res.Result);
        AssertErrorBody(Body(res), expectedCode: null);
        Assert.Equal(2, StoredMaxAttempts(tdb, camp.Id));
    }

    [Fact]
    public async Task Update_Draft_GiamMaxAttempts_TuDo()
    {
        using var tdb = new CampaignTestDb();
        var org = Guid.NewGuid();
        var camp = Seed(tdb.Db, org, CampaignStatus.Draft, maxAttempts: 3);

        var res = await NewController(NewService(tdb.NewContext()), org).UpdateCampaign(
            camp.Id, new UpdateCampaignRequest { Title = camp.Title, MaxAttempts = 1 }, default);

        Assert.IsType<OkObjectResult>(res.Result);
        Assert.Equal(1, StoredMaxAttempts(tdb, camp.Id));
    }

    [Fact]
    public async Task Update_Draft_DoiTimeLimit_TuDo()
    {
        using var tdb = new CampaignTestDb();
        var org = Guid.NewGuid();
        var camp = Seed(tdb.Db, org, CampaignStatus.Draft, timeLimit: 30);

        var res = await NewController(NewService(tdb.NewContext()), org).UpdateCampaign(
            camp.Id, new UpdateCampaignRequest { Title = camp.Title, TimeLimitMinutes = 45 }, default);

        Assert.IsType<OkObjectResult>(res.Result);
        Assert.Equal(45, StoredTimeLimit(tdb, camp.Id));
    }

    // ── [C2] Active: chỉ TĂNG ───────────────────────────────────────────────────────────────────

    [Fact]
    public async Task Update_Active_TangMaxAttempts1Len2_200_Luu()
    {
        using var tdb = new CampaignTestDb();
        var org = Guid.NewGuid();
        var camp = Seed(tdb.Db, org, CampaignStatus.Active, maxAttempts: 1);

        var res = await NewController(NewService(tdb.NewContext()), org).UpdateCampaign(
            camp.Id, new UpdateCampaignRequest { Title = camp.Title, MaxAttempts = 2 }, default);

        Assert.IsType<OkObjectResult>(res.Result);
        Assert.Equal(2, Body(res).GetProperty("maxAttempts").GetInt32());
        Assert.Equal(2, StoredMaxAttempts(tdb, camp.Id));
    }

    [Fact]
    public async Task Update_Active_GiamMaxAttempts2Xuong1_409_MAX_ATTEMPTS_DECREASE_KhongLuu()
    {
        using var tdb = new CampaignTestDb();
        var org = Guid.NewGuid();
        var camp = Seed(tdb.Db, org, CampaignStatus.Active, maxAttempts: 2);

        var res = await NewController(NewService(tdb.NewContext()), org).UpdateCampaign(
            camp.Id, new UpdateCampaignRequest { Title = camp.Title, MaxAttempts = 1 }, default);

        Assert.IsType<ConflictObjectResult>(res.Result);
        AssertErrorBody(Body(res), "MAX_ATTEMPTS_DECREASE");
        Assert.Equal(2, StoredMaxAttempts(tdb, camp.Id));
    }

    [Fact]
    public async Task Update_Active_GuiLaiDungMaxAttempts_200_KhongDoi()
    {
        using var tdb = new CampaignTestDb();
        var org = Guid.NewGuid();
        var camp = Seed(tdb.Db, org, CampaignStatus.Active, maxAttempts: 2);

        var res = await NewController(NewService(tdb.NewContext()), org).UpdateCampaign(
            camp.Id, new UpdateCampaignRequest { Title = camp.Title, MaxAttempts = 2 }, default);

        Assert.IsType<OkObjectResult>(res.Result);
        Assert.Equal(2, StoredMaxAttempts(tdb, camp.Id));
    }

    // PUT với body CHỈ { title, maxAttempts } — JSON thật (camelCase), mọi field khác vắng = KHÔNG ĐỔI.
    [Fact]
    public async Task Update_Active_BodyChiTitleVaMaxAttempts_200_FieldKhacGiuNguyen()
    {
        using var tdb = new CampaignTestDb();
        var org = Guid.NewGuid();
        var camp = Seed(tdb.Db, org, CampaignStatus.Active, maxAttempts: 1, timeLimit: 45);

        var req = JsonSerializer.Deserialize<UpdateCampaignRequest>("""{"title":"Đổi tên","maxAttempts":3}""", Web)!;
        Assert.Equal(3, req.MaxAttempts);
        Assert.Null(req.TimeLimitMinutes);

        var res = await NewController(NewService(tdb.NewContext()), org).UpdateCampaign(camp.Id, req, default);

        Assert.IsType<OkObjectResult>(res.Result);
        var stored = tdb.NewContext().Campaigns.AsNoTracking().Single(c => c.Id == camp.Id);
        Assert.Equal(3, stored.MaxAttempts);
        Assert.Equal(45, stored.TimeLimitMinutes);
        Assert.Equal("Đổi tên", stored.Title);
    }

    // ── [C3] thời lượng khoá ngoài Draft ────────────────────────────────────────────────────────

    [Theory]
    [InlineData(CampaignStatus.Active)]
    [InlineData(CampaignStatus.Closed)]
    public async Task Update_KhongPhaiDraft_DoiTimeLimit_409_TIME_LIMIT_LOCKED_KhongLuu(CampaignStatus status)
    {
        using var tdb = new CampaignTestDb();
        var org = Guid.NewGuid();
        var camp = Seed(tdb.Db, org, status, timeLimit: 30);

        var res = await NewController(NewService(tdb.NewContext()), org).UpdateCampaign(
            camp.Id, new UpdateCampaignRequest { Title = camp.Title, TimeLimitMinutes = 45 }, default);

        Assert.IsType<ConflictObjectResult>(res.Result);
        AssertErrorBody(Body(res), "TIME_LIMIT_LOCKED");
        Assert.Equal(30, StoredTimeLimit(tdb, camp.Id));
    }

    [Fact]
    public async Task Update_Active_GuiLaiDungTimeLimit_200()
    {
        using var tdb = new CampaignTestDb();
        var org = Guid.NewGuid();
        var camp = Seed(tdb.Db, org, CampaignStatus.Active, timeLimit: 30);

        var res = await NewController(NewService(tdb.NewContext()), org).UpdateCampaign(
            camp.Id, new UpdateCampaignRequest { Title = camp.Title, TimeLimitMinutes = 30 }, default);

        Assert.IsType<OkObjectResult>(res.Result);
        Assert.Equal(30, StoredTimeLimit(tdb, camp.Id));
    }

    // Chiến dịch Active mang thời lượng CŨ ngoài dải (trước ATT1 không validate) mà wizard echo lại đúng
    // giá trị đó ⇒ KHÔNG 400: thời lượng đã khoá nên HR không sửa được — 400 ở mọi lần Lưu là kẹt cứng.
    [Fact]
    public async Task Update_Active_EchoTimeLimitCuNgoaiDai_200_KhongKetCung()
    {
        using var tdb = new CampaignTestDb();
        var org = Guid.NewGuid();
        var camp = Seed(tdb.Db, org, CampaignStatus.Active, maxAttempts: 1, timeLimit: 0);

        var res = await NewController(NewService(tdb.NewContext()), org).UpdateCampaign(
            camp.Id, new UpdateCampaignRequest { Title = camp.Title, TimeLimitMinutes = 0, MaxAttempts = 2 }, default);

        Assert.IsType<OkObjectResult>(res.Result);
        Assert.Equal(2, StoredMaxAttempts(tdb, camp.Id));
        Assert.Equal(0, StoredTimeLimit(tdb, camp.Id));
    }

    // ── [C2] Closed/Archived: đổi số lượt → 409 { error } (không code) ──────────────────────────

    [Theory]
    [InlineData(CampaignStatus.Closed, 2)]
    [InlineData(CampaignStatus.Closed, 3)]
    [InlineData(CampaignStatus.Archived, 3)]
    public async Task Update_DaDong_DoiMaxAttempts_409_BodyError_KhongCode(CampaignStatus status, int requested)
    {
        using var tdb = new CampaignTestDb();
        var org = Guid.NewGuid();
        var camp = Seed(tdb.Db, org, status, maxAttempts: 1);

        var res = await NewController(NewService(tdb.NewContext()), org).UpdateCampaign(
            camp.Id, new UpdateCampaignRequest { Title = camp.Title, MaxAttempts = requested }, default);

        Assert.IsType<ConflictObjectResult>(res.Result);
        AssertErrorBody(Body(res), expectedCode: null);
        Assert.Equal(1, StoredMaxAttempts(tdb, camp.Id));
    }

    [Fact]
    public async Task Update_Closed_GuiLaiDungMaxAttempts_200()
    {
        using var tdb = new CampaignTestDb();
        var org = Guid.NewGuid();
        var camp = Seed(tdb.Db, org, CampaignStatus.Closed, maxAttempts: 2);

        var res = await NewController(NewService(tdb.NewContext()), org).UpdateCampaign(
            camp.Id, new UpdateCampaignRequest { Title = camp.Title, MaxAttempts = 2, TimeLimitMinutes = 30 }, default);

        Assert.IsType<OkObjectResult>(res.Result);
    }

    // ── [C4] publish ────────────────────────────────────────────────────────────────────────────

    private static Campaign SeedPublishable(CampaignDbContext db, Guid org, int? timeLimit)
    {
        var c = CampaignTestDb.NewCampaign(org, CampaignStatus.Draft);
        c.Domain = "BE";
        c.TimeLimitMinutes = timeLimit;
        c.Questions.Add(new CampaignQuestion
        {
            Id = Guid.NewGuid(), CampaignId = c.Id, OrgId = org, QuestionText = "Q1",
            Source = QuestionSource.CustomHr, IsRequired = true, CreatedAt = DateTime.UtcNow,
        });
        db.Campaigns.Add(c);
        db.SaveChanges();
        return c;
    }

    [Theory]
    [InlineData(null)]
    [InlineData(0)]
    [InlineData(4)]
    [InlineData(181)]
    [InlineData(100000)]
    public async Task Publish_TimeLimitNullHoacNgoaiDai_400_BodyError_GiuDraft_KhongGoiAi(int? minutes)
    {
        using var tdb = new CampaignTestDb();
        var org = Guid.NewGuid();
        var camp = SeedPublishable(tdb.Db, org, minutes);
        var suggester = new Mock<ICriteriaSuggester>();

        var res = await NewController(NewService(tdb.NewContext(), suggester.Object), org).PublishCampaign(camp.Id, default);

        Assert.IsType<BadRequestObjectResult>(res.Result);
        AssertErrorBody(Body(res), expectedCode: null);
        var stored = tdb.NewContext().Campaigns.AsNoTracking().Single(c => c.Id == camp.Id);
        Assert.Equal(CampaignStatus.Draft, stored.Status);
        // 400 không được đốt token: kiểm thời lượng đứng TRƯỚC mọi lời gọi AI của publish.
        suggester.VerifyNoOtherCalls();
    }

    [Theory]
    [InlineData(5)]
    [InlineData(180)]
    public async Task Publish_TimeLimitBien_ChoQua(int minutes)
    {
        using var tdb = new CampaignTestDb();
        var org = Guid.NewGuid();
        var camp = SeedPublishable(tdb.Db, org, minutes);

        var res = await NewController(NewService(tdb.NewContext()), org).PublishCampaign(camp.Id, default);

        Assert.IsType<OkObjectResult>(res.Result);
        Assert.Equal(CampaignStatus.Active,
            tdb.NewContext().Campaigns.AsNoTracking().Single(c => c.Id == camp.Id).Status);
    }

    // ── [C5] response mang maxAttempts ──────────────────────────────────────────────────────────

    [Fact]
    public async Task GetChiTiet_VaDanhSach_CoMaxAttempts_TrongJson()
    {
        using var tdb = new CampaignTestDb();
        var org = Guid.NewGuid();
        var camp = Seed(tdb.Db, org, CampaignStatus.Active, maxAttempts: 3);
        var controller = NewController(NewService(tdb.NewContext()), org);

        var detail = await controller.GetCampaignById(camp.Id, default);
        Assert.Equal(3, Body(detail).GetProperty("maxAttempts").GetInt32());

        var page = await NewService(tdb.NewContext()).GetCampaignsAsync(org, null, null, default);
        var item = JsonSerializer.SerializeToElement(page.Items.Single(), Web);
        Assert.Equal(3, item.GetProperty("maxAttempts").GetInt32());
    }

    // ── CHECK DB: max_attempts ∈ [1, 3] (SQLite CÓ enforce CHECK khai ở model) ──────────────────

    [Fact]
    public void Db_CheckMaxAttemptsNgoaiDai_BiChan()
    {
        using var tdb = new CampaignTestDb();
        var c = CampaignTestDb.NewCampaign(Guid.NewGuid());
        c.MaxAttempts = 4;
        tdb.Db.Campaigns.Add(c);

        Assert.Throws<DbUpdateException>(() => tdb.Db.SaveChanges());
    }
}
