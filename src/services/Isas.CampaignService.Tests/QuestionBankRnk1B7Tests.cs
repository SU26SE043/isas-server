using System.Text.Json;
using Isas.CampaignService.DTOs;
using Isas.CampaignService.Models;
using Isas.CampaignService.Services;
using Microsoft.AspNetCore.Http;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging;
using Moq;
using Xunit;

using CampaignSvc = Isas.CampaignService.Services.CampaignService;

namespace Isas.CampaignService.Tests;

/// <summary>
/// RNK1 · HĐ-8 — NGÂN HÀNG ĐỀ có hàng rào + tóm tắt.
/// <list type="bullet">
/// <item><c>questions_per_session</c> (K) ∈ [1, 20]; PUT <c>0</c> = RESET (SET NULL); đổi K ngoài
///   <c>Draft</c> ⇒ 409 (cùng luật <c>PUT /questions</c>).</item>
/// <item><see cref="QuestionBankSummary"/> read-time trên <see cref="CampaignResponse"/>: total /
///   alwaysAsked / K / groups (null/"" → "Chung", gộp hoa-thường) / warnings.</item>
/// <item>publish: warnings không rỗng ⇒ 400 <c>{ code:"QUESTION_BANK_INVALID", warnings }</c>.</item>
/// </list>
/// </summary>
public class QuestionBankRnk1B7Tests
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

    private static readonly DateTime SeedEpoch = new(2026, 1, 1, 0, 0, 0, DateTimeKind.Utc);

    private static CampaignQuestion Q(Guid campaignId, Guid orgId, string text, bool required = false, string? group = null)
        => new()
        {
            Id = Guid.NewGuid(), CampaignId = campaignId, OrgId = orgId, QuestionText = text,
            Source = QuestionSource.CustomHr, IsRequired = required, QuestionGroup = group,
            CreatedAt = DateTime.UtcNow,
        };

    private static Campaign Seed(
        CampaignDbContext db, Guid org, CampaignStatus status = CampaignStatus.Draft,
        int? questionsPerSession = null, int? maxDeepPerQuestion = null, int? maxQuestions = null,
        params CampaignQuestion[] questions)
    {
        var c = CampaignTestDb.NewCampaign(org, status);
        c.Domain = "BE";
        c.QuestionsPerSession = questionsPerSession;
        c.MaxDeepPerQuestion = maxDeepPerQuestion;
        c.MaxQuestions = maxQuestions;
        var i = 0;
        foreach (var q in questions)
        {
            q.CampaignId = c.Id; q.OrgId = org;
            q.CreatedAt = SeedEpoch.AddSeconds(i++);   // thứ tự nạp tất định (casing nhóm = câu sớm nhất)
            c.Questions.Add(q);
        }
        c.Criteria.Add(new CampaignCriterion
        {
            Id = Guid.NewGuid(), CampaignId = c.Id, OrderNo = 0, Name = "A", Weight = 1.0m, MaxScore = 5,
            Source = CriterionSource.HrEdited, CreatedAt = DateTime.UtcNow, UpdatedAt = DateTime.UtcNow,
        });
        db.Campaigns.Add(c);
        db.SaveChanges();
        return c;
    }

    private static CreateCampaignRequest BaseCreate(params string[] questionTexts) => new()
    {
        Title = "C", Domain = "BE", TimeLimitMinutes = 30,
        StartsAt = DateTime.UtcNow.AddMinutes(5), ExpiresAt = DateTime.UtcNow.AddDays(2),
        Questions = questionTexts.Select(t => new QuestionItem { QuestionText = t, IsRequired = true }).ToList(),
    };

    // ── K ∈ [1, 20] ────────────────────────────────────────────────────────────────────────────

    [Theory]
    [InlineData(21)]
    [InlineData(100000)]
    public async Task Create_KVuot20_400_KemSo(int k)
    {
        using var tdb = new CampaignTestDb();
        var org = Guid.NewGuid();
        var req = BaseCreate("Q1");
        req.QuestionsPerSession = k;

        var ex = await Assert.ThrowsAsync<ArgumentException>(() =>
            NewService(tdb.NewContext()).CreateCampaignAsync(org, org, req, default));
        Assert.Contains(k.ToString(), ex.Message);
        Assert.Contains("[1, 20]", ex.Message);
    }

    [Fact]
    public async Task Create_K0_400()
    {
        using var tdb = new CampaignTestDb();
        var org = Guid.NewGuid();
        var req = BaseCreate("Q1");
        req.QuestionsPerSession = 0;

        await Assert.ThrowsAsync<ArgumentException>(() =>
            NewService(tdb.NewContext()).CreateCampaignAsync(org, org, req, default));
    }

    // ── PUT 0 = RESET (SET NULL) ───────────────────────────────────────────────────────────────
    [Fact]
    public async Task Update_K0_SetNull_DuongReset()
    {
        using var tdb = new CampaignTestDb();
        var org = Guid.NewGuid();
        var camp = Seed(tdb.Db, org, questionsPerSession: 3,
            questions: new[] { Q(Guid.Empty, org, "Q1"), Q(Guid.Empty, org, "Q2"), Q(Guid.Empty, org, "Q3") });

        var res = await NewService(tdb.NewContext()).UpdateCampaignAsync(org, org, camp.Id,
            new UpdateCampaignRequest { QuestionsPerSession = 0 }, default);

        Assert.Null(res.QuestionBank.QuestionsPerSession);
        Assert.Null(tdb.NewContext().Campaigns.Single(c => c.Id == camp.Id).QuestionsPerSession);
    }

    [Fact]
    public async Task Update_KNull_KhongDoi()
    {
        using var tdb = new CampaignTestDb();
        var org = Guid.NewGuid();
        var camp = Seed(tdb.Db, org, questionsPerSession: 3, questions: new[] { Q(Guid.Empty, org, "Q1") });

        await NewService(tdb.NewContext()).UpdateCampaignAsync(org, org, camp.Id,
            new UpdateCampaignRequest { Title = "Đổi tên" }, default);

        Assert.Equal(3, tdb.NewContext().Campaigns.Single(c => c.Id == camp.Id).QuestionsPerSession);
    }

    // ── đổi K ngoài Draft ⇒ 409 ────────────────────────────────────────────────────────────────
    [Fact]
    public async Task Update_KDoi_KhiActive_409()
    {
        using var tdb = new CampaignTestDb();
        var org = Guid.NewGuid();
        var camp = Seed(tdb.Db, org, CampaignStatus.Active, questionsPerSession: null,
            questions: new[] { Q(Guid.Empty, org, "Q1"), Q(Guid.Empty, org, "Q2"), Q(Guid.Empty, org, "Q3") });

        await Assert.ThrowsAsync<InvalidOperationException>(() =>
            NewService(tdb.NewContext()).UpdateCampaignAsync(org, org, camp.Id,
                new UpdateCampaignRequest { QuestionsPerSession = 2 }, default));

        Assert.Null(tdb.NewContext().Campaigns.Single(c => c.Id == camp.Id).QuestionsPerSession);   // không đổi
    }

    [Fact]
    public async Task Update_KKhongDoi_KhiActive_KhongNem()
    {
        using var tdb = new CampaignTestDb();
        var org = Guid.NewGuid();
        var camp = Seed(tdb.Db, org, CampaignStatus.Active, questionsPerSession: 3,
            questions: new[] { Q(Guid.Empty, org, "Q1"), Q(Guid.Empty, org, "Q2"), Q(Guid.Empty, org, "Q3") });

        var res = await NewService(tdb.NewContext()).UpdateCampaignAsync(org, org, camp.Id,
            new UpdateCampaignRequest { QuestionsPerSession = 3 }, default);   // no-op

        Assert.Equal(3, res.QuestionBank.QuestionsPerSession);
    }

    // ── CHECK ck_campaigns_questions_per_session_positive (0 KHÔNG được chạm DB) ─────────────────
    [Fact]
    public async Task Guard_CHECK_QuestionsPerSession0_ViPhamRangBuoc()
    {
        using var tdb = new CampaignTestDb();
        var camp = CampaignTestDb.NewCampaign(Guid.NewGuid());
        camp.QuestionsPerSession = 0;   // vi phạm `questions_per_session IS NULL OR >= 1`
        tdb.Db.Campaigns.Add(camp);

        await Assert.ThrowsAsync<DbUpdateException>(() => tdb.Db.SaveChangesAsync());
    }

    // ── QuestionBankSummary read-time ──────────────────────────────────────────────────────────
    [Fact]
    public async Task Summary_Groups_AlwaysAsked_Dung()
    {
        using var tdb = new CampaignTestDb();
        var org = Guid.NewGuid();
        var camp = Seed(tdb.Db, org, questionsPerSession: 6, questions: new[]
        {
            Q(Guid.Empty, org, "r1", required: true),
            Q(Guid.Empty, org, "r2", required: true),
            Q(Guid.Empty, org, "a1", group: "Thuật toán"),
            Q(Guid.Empty, org, "a2", group: "THUẬT TOÁN"),   // gộp hoa-thường
            Q(Guid.Empty, org, "g1", group: "Giao tiếp"),
            Q(Guid.Empty, org, "n1", group: null),           // → "Chung"
            Q(Guid.Empty, org, "n2", group: "  "),           // whitespace → "Chung"
        });

        var res = await NewService(tdb.NewContext()).UpdateCampaignAsync(org, org, camp.Id,
            new UpdateCampaignRequest { Title = "x" }, default);
        var s = res.QuestionBank;

        Assert.Equal(7, s.Total);
        Assert.Equal(2, s.AlwaysAsked);
        Assert.Equal(6, s.QuestionsPerSession);

        // "Chung" đứng đầu; "Thuật toán" gộp 2 câu.
        Assert.Equal(new[] { "Chung", "Giao tiếp", "Thuật toán" }, s.Groups.Select(g => g.Name));
        Assert.Equal(4, s.Groups.Single(g => g.Name == "Chung").Count);   // r1,r2 + n1(null) + n2("  ") → Chung
        Assert.Equal(1, s.Groups.Single(g => g.Name == "Giao tiếp").Count);
        Assert.Equal(2, s.Groups.Single(g => g.Name == "Thuật toán").Count);   // "Thuật toán" + "THUẬT TOÁN"
        Assert.Empty(s.Warnings);
    }

    [Fact]
    public async Task Summary_Warning_KLonHonTotal()
    {
        using var tdb = new CampaignTestDb();
        var org = Guid.NewGuid();
        var camp = Seed(tdb.Db, org, questionsPerSession: 10,
            questions: new[] { Q(Guid.Empty, org, "Q1"), Q(Guid.Empty, org, "Q2") });

        var s = (await NewService(tdb.NewContext()).UpdateCampaignAsync(org, org, camp.Id,
            new UpdateCampaignRequest { Title = "x" }, default)).QuestionBank;

        Assert.Contains(s.Warnings, w => w.Contains("questions_per_session (10)") && w.Contains("(2)"));
    }

    [Fact]
    public async Task Summary_Warning_AlwaysAskedLonHonK()
    {
        using var tdb = new CampaignTestDb();
        var org = Guid.NewGuid();
        var camp = Seed(tdb.Db, org, questionsPerSession: 2, questions: new[]
        {
            Q(Guid.Empty, org, "r1", required: true),
            Q(Guid.Empty, org, "r2", required: true),
            Q(Guid.Empty, org, "r3", required: true),
            Q(Guid.Empty, org, "o1"),
        });

        var s = (await NewService(tdb.NewContext()).UpdateCampaignAsync(org, org, camp.Id,
            new UpdateCampaignRequest { Title = "x" }, default)).QuestionBank;

        Assert.Contains(s.Warnings, w => w.Contains("bắt buộc (3)") && w.Contains("(2)"));
    }

    [Fact]
    public async Task Summary_Warning_NganSachAdaptive()
    {
        using var tdb = new CampaignTestDb();
        var org = Guid.NewGuid();
        // K=5 (từ số câu), d=3, T=19 ⇒ 5×4 = 20 > 19.
        var camp = Seed(tdb.Db, org, maxDeepPerQuestion: 3, maxQuestions: 19,
            questions: Enumerable.Range(0, 5).Select(i => Q(Guid.Empty, org, $"Q{i}")).ToArray());

        var s = (await NewService(tdb.NewContext()).UpdateCampaignAsync(org, org, camp.Id,
            new UpdateCampaignRequest { Title = "x" }, default)).QuestionBank;

        Assert.Contains(s.Warnings, w => w.Contains("= 20 câu"));
    }

    // ── publish: warnings không rỗng ⇒ 400 QUESTION_BANK_INVALID ────────────────────────────────
    [Fact]
    public async Task Publish_CoWarning_Nem_QuestionBankInvalid_GiuDraft()
    {
        using var tdb = new CampaignTestDb();
        var org = Guid.NewGuid();
        // alwaysAsked = 3 > K = 2, KHÔNG có vấn đề ngân sách adaptive (d=0).
        var camp = Seed(tdb.Db, org, questionsPerSession: 2, questions: new[]
        {
            Q(Guid.Empty, org, "r1", required: true),
            Q(Guid.Empty, org, "r2", required: true),
            Q(Guid.Empty, org, "r3", required: true),
        });

        var ex = await Assert.ThrowsAsync<QuestionBankInvalidException>(() =>
            NewService(tdb.NewContext()).PublishCampaignAsync(org, org, camp.Id, default));

        var body = JsonSerializer.SerializeToElement(ex.Body);
        Assert.Equal("QUESTION_BANK_INVALID", body.GetProperty("code").GetString());
        Assert.True(body.GetProperty("warnings").GetArrayLength() >= 1);

        Assert.Equal(CampaignStatus.Draft,
            tdb.NewContext().Campaigns.AsNoTracking().Single(c => c.Id == camp.Id).Status);
    }

    [Fact]
    public async Task Publish_KhongWarning_ChoQua()
    {
        using var tdb = new CampaignTestDb();
        var org = Guid.NewGuid();
        var camp = Seed(tdb.Db, org, questionsPerSession: 3, questions: new[]
        {
            Q(Guid.Empty, org, "Q1"), Q(Guid.Empty, org, "Q2"), Q(Guid.Empty, org, "Q3"),
        });

        var res = await NewService(tdb.NewContext()).PublishCampaignAsync(org, org, camp.Id, default);

        Assert.Equal("Active", res.Status);
        Assert.Empty(res.QuestionBank.Warnings);
    }

    // ── questionBank ĐÚNG trên MỌI CampaignResponse — kể cả các load KHÔNG .Include(Questions) ────
    // Trước fix: 4 load trần ⇒ c.Questions rỗng ⇒ questionBank.total = 0 + cảnh báo giả.

    [Fact]
    public async Task Rnk1B7_UploadFiles_Response_QuestionBank_DemDungCau()
    {
        using var tdb = new CampaignTestDb();
        var org = Guid.NewGuid();
        var camp = Seed(tdb.Db, org, questionsPerSession: 3, questions: new[]
        {
            Q(Guid.Empty, org, "Q1", required: true), Q(Guid.Empty, org, "Q2"), Q(Guid.Empty, org, "Q3"),
        });

        // UploadCampaignFilesRequest rỗng ⇒ no-op nhưng vẫn trả FromEntity(campaign).
        var res = await NewService(tdb.NewContext())
            .UploadCampaignFilesAsync(org, camp.Id, new UploadCampaignFilesRequest(), default);

        Assert.Equal(3, res.QuestionBank.Total);
        Assert.Equal(1, res.QuestionBank.AlwaysAsked);
        Assert.Empty(res.QuestionBank.Warnings);
    }

    [Fact]
    public async Task Rnk1B7_UpdateFiles_Response_QuestionBank_DemDungCau()
    {
        using var tdb = new CampaignTestDb();
        var org = Guid.NewGuid();
        var camp = Seed(tdb.Db, org, questionsPerSession: 3, questions: new[]
        {
            Q(Guid.Empty, org, "Q1"), Q(Guid.Empty, org, "Q2"), Q(Guid.Empty, org, "Q3"),
        });
        camp.JDText = "JD có sẵn";   // ⇒ file JD bị lọc bỏ (HasDirectText) ⇒ no-op, vẫn trả FromEntity
        tdb.Db.SaveChanges();

        var res = await NewService(tdb.NewContext()).UpdateCampaignFilesAsync(
            org, camp.Id, new UploadCampaignFilesRequest { JdFile = Mock.Of<IFormFile>() }, default);

        Assert.Equal(3, res.QuestionBank.Total);
        Assert.Empty(res.QuestionBank.Warnings);
    }
}
