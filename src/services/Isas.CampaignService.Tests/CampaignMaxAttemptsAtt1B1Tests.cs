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

    private static CampaignSvc NewService(CampaignDbContext db, ICriteriaSuggester? suggester = null,
        IJobNeedsSuggester? jobNeeds = null) =>
        new(db, Mock.Of<IFileService>(), Mock.Of<ILogger<CampaignSvc>>(), Mock.Of<IParserService>(),
            suggester ?? Mock.Of<ICriteriaSuggester>(), Mock.Of<IInvitationEmailPublisher>(),
            entitlements: Entitlements(), jobNeedsSuggester: jobNeeds);

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

    // ── fix(att1-b1) — lỗ test do KIỂM B1 tìm ra (6 nhóm, 12 ca). Mỗi nhóm khoá một phép mutation
    //    mà bộ test cũ để XANH; nhãn K* là mã phép của người kiểm (scratchpad att1/kiem-b1/spec.json).

    // K1 — ngoài Draft, maxAttempts ngoài [1, 3] vẫn 400 { error } KHÔNG code và không lưu. Nếu chỉ kiểm
    // dải khi Draft thì Active 1→4 lọt tầng app rồi nổ CHECK DB ⇒ 500 thay vì 400.
    [Theory]
    [InlineData(CampaignStatus.Active, 1, 4)]
    [InlineData(CampaignStatus.Active, 2, 0)]
    [InlineData(CampaignStatus.Closed, 1, 4)]
    [InlineData(CampaignStatus.Archived, 2, 0)]
    public async Task Update_KhongPhaiDraft_MaxAttemptsNgoaiDai_400_KhongCode_KhongLuu(
        CampaignStatus status, int stored, int sent)
    {
        using var tdb = new CampaignTestDb();
        var org = Guid.NewGuid();
        var camp = Seed(tdb.Db, org, status, maxAttempts: stored, timeLimit: 30);

        var res = await NewController(NewService(tdb.NewContext()), org).UpdateCampaign(
            camp.Id, new UpdateCampaignRequest { Title = camp.Title, MaxAttempts = sent }, default);

        Assert.IsType<BadRequestObjectResult>(res.Result);
        AssertErrorBody(Body(res), expectedCode: null);
        Assert.Equal(stored, StoredMaxAttempts(tdb, camp.Id));
    }

    // K2 — vắng maxAttempts = KHÔNG ĐỔI ở MỌI trạng thái, kể cả khi giá trị đang lưu ≠ 1 (JSON thật chỉ có
    // title). Các ca "vắng" cũ đều seed 1 nên phép "vắng hiểu là 1" chạy qua XANH.
    [Theory]
    [InlineData(CampaignStatus.Draft)]
    [InlineData(CampaignStatus.Active)]
    [InlineData(CampaignStatus.Closed)]
    [InlineData(CampaignStatus.Archived)]
    public async Task Update_VangMaxAttempts_GiuNguyen3_MoiTrangThai(CampaignStatus status)
    {
        using var tdb = new CampaignTestDb();
        var org = Guid.NewGuid();
        var camp = Seed(tdb.Db, org, status, maxAttempts: 3, timeLimit: 30);
        var req = JsonSerializer.Deserialize<UpdateCampaignRequest>("""{"title":"Đổi tên"}""", Web)!;
        Assert.Null(req.MaxAttempts);

        var res = await NewController(NewService(tdb.NewContext()), org).UpdateCampaign(camp.Id, req, default);

        Assert.IsType<OkObjectResult>(res.Result);
        Assert.Equal(3, Body(res).GetProperty("maxAttempts").GetInt32());
        Assert.Equal(3, StoredMaxAttempts(tdb, camp.Id));
    }

    // K3 — Archived đổi thời lượng ⇒ 409 TIME_LIMIT_LOCKED (khoá phải phủ MỌI trạng thái khác Draft).
    [Fact]
    public async Task Update_Archived_DoiTimeLimit_409_TIME_LIMIT_LOCKED_KhongLuu()
    {
        using var tdb = new CampaignTestDb();
        var org = Guid.NewGuid();
        var camp = Seed(tdb.Db, org, CampaignStatus.Archived, maxAttempts: 1, timeLimit: 30);

        var res = await NewController(NewService(tdb.NewContext()), org).UpdateCampaign(
            camp.Id, new UpdateCampaignRequest { Title = camp.Title, TimeLimitMinutes = 45 }, default);

        Assert.IsType<ConflictObjectResult>(res.Result);
        AssertErrorBody(Body(res), "TIME_LIMIT_LOCKED");
        Assert.Equal(30, StoredTimeLimit(tdb, camp.Id));
    }

    // K4 — Archived gửi lại ĐÚNG maxAttempts + thời lượng đang lưu ⇒ 200 (wizard echo cả form).
    [Fact]
    public async Task Update_Archived_GuiLaiDungGiaTri_200()
    {
        using var tdb = new CampaignTestDb();
        var org = Guid.NewGuid();
        var camp = Seed(tdb.Db, org, CampaignStatus.Archived, maxAttempts: 2, timeLimit: 30);

        var res = await NewController(NewService(tdb.NewContext()), org).UpdateCampaign(
            camp.Id, new UpdateCampaignRequest { Title = camp.Title, MaxAttempts = 2, TimeLimitMinutes = 30 }, default);

        Assert.IsType<OkObjectResult>(res.Result);
    }

    // K5 — nháp cũ đang lưu thời lượng null: PUT 30 phải LƯU 30. Đây là đường cứu DUY NHẤT cho nháp mà
    // publish [C4] đang chặn; coi null là "bằng" thì PUT thành no-op câm, nháp kẹt vĩnh viễn.
    [Fact]
    public async Task Update_Draft_TimeLimitNull_Put30_Luu30()
    {
        using var tdb = new CampaignTestDb();
        var org = Guid.NewGuid();
        var camp = Seed(tdb.Db, org, CampaignStatus.Draft, maxAttempts: 1, timeLimit: null);

        var res = await NewController(NewService(tdb.NewContext()), org).UpdateCampaign(
            camp.Id, new UpdateCampaignRequest { Title = camp.Title, TimeLimitMinutes = 30 }, default);

        Assert.IsType<OkObjectResult>(res.Result);
        Assert.Equal(30, StoredTimeLimit(tdb, camp.Id));
    }

    // K10 — publish thời lượng null, campaign ĐÃ có tiêu chí HR + JD nhưng chưa có job needs ⇒ 400 và
    // KHÔNG gọi AI job-needs. Ca publish cũ không truyền IJobNeedsSuggester nên không phủ được lời gọi này
    // (và đã có tiêu chí thì criteria suggester cũng không được gọi — VerifyNoOtherCalls trên nó vô nghĩa).
    [Fact]
    public async Task Publish_TimeLimitNull_CoSanTieuChiVaJd_400_KhongGoiAiJobNeeds()
    {
        using var tdb = new CampaignTestDb();
        var org = Guid.NewGuid();
        var c = CampaignTestDb.NewCampaign(org, CampaignStatus.Draft);
        c.Domain = "BE";
        c.TimeLimitMinutes = null;
        c.JDText = "Backend developer .NET, PostgreSQL, 3 năm kinh nghiệm.";
        c.Questions.Add(new CampaignQuestion
        {
            Id = Guid.NewGuid(), CampaignId = c.Id, OrgId = org, QuestionText = "Q1",
            Source = QuestionSource.CustomHr, IsRequired = true, CreatedAt = DateTime.UtcNow,
        });
        c.Criteria.Add(new CampaignCriterion
        {
            Id = Guid.NewGuid(), CampaignId = c.Id, OrderNo = 1, Name = "Kỹ thuật", Weight = 1m, MaxScore = 10,
            Source = CriterionSource.HrEdited, CreatedAt = DateTime.UtcNow, UpdatedAt = DateTime.UtcNow,
        });
        tdb.Db.Campaigns.Add(c);
        tdb.Db.SaveChanges();
        var jobNeeds = new Mock<IJobNeedsSuggester>();

        var res = await NewController(NewService(tdb.NewContext(), jobNeeds: jobNeeds.Object), org)
            .PublishCampaign(c.Id, default);

        Assert.IsType<BadRequestObjectResult>(res.Result);
        jobNeeds.VerifyNoOtherCalls();
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
