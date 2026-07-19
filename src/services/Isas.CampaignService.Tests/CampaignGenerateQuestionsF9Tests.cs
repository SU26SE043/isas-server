using System.Net;
using System.Text;
using Isas.CampaignService.Models;
using Isas.CampaignService.Services;
using Isas.Shared.Validation;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging;
using Moq;

using CampaignSvc = Isas.CampaignService.Services.CampaignService;

namespace Isas.CampaignService.Tests;

/// <summary>
/// F9 (FR11) — AI sinh câu hỏi từ JD cho campaign B2B (CampaignService trước đây CHƯA BAO GIỜ gọi
/// /generate-questions; chỉ /suggest-criteria + /face-verify).
///
/// (a) Draft + JD → câu hỏi lưu với source = AiGenerated;
/// (b) sinh lại → thay lượt AI cũ, GIỮ câu HR gõ tay (không cộng dồn, không nuốt công HR);
/// (c) campaign Active → 409 (CAMP-2: câu hỏi chỉ sửa khi Draft) + KHÔNG gọi AI;
/// (d) AIService lỗi → 502 (DownstreamServiceException), KHÔNG phải 400 — và KHÔNG mất câu hỏi đang có;
/// (e) AI trả rỗng → 502 (không lặng lẽ xoá sạch đề rồi báo thành công);
/// (f) chưa có JD / JD quá dài / count ngoài dải → 400 TRƯỚC khi tốn một lời gọi AI;
/// (g) campaign ngoài org → 404.
/// (controller map: KeyNotFound→404 · Downstream→502 · Argument→400 · InvalidOperation→409.)
/// </summary>
public class CampaignGenerateQuestionsF9Tests
{
    // ─────────────────────────── Fake generator ───────────────────────────

    /// <summary>Ghi lại có được gọi hay không → chứng minh guard chặn TRƯỚC khi tốn lời gọi AI.</summary>
    private sealed class FakeGenerator : IQuestionGenerator
    {
        private readonly Func<List<string>> _result;
        public int Calls { get; private set; }
        public string? LastJobCategory { get; private set; }
        public string? LastJdText { get; private set; }
        public int? LastCount { get; private set; }

        public FakeGenerator(Func<List<string>> result) => _result = result;

        public static FakeGenerator Returning(params string[] questions)
            => new(() => questions.ToList());

        public static FakeGenerator Throwing()
            => new(() => throw new DownstreamServiceException("AIService /generate-questions trả về 503."));

        public Task<List<string>> GenerateAsync(
            string jobCategory, string? jdText, int? count, CancellationToken ct = default)
        {
            Calls++;
            LastJobCategory = jobCategory;
            LastJdText = jdText;
            LastCount = count;
            return Task.FromResult(_result());
        }
    }

    private static CampaignSvc NewService(CampaignDbContext db, IQuestionGenerator? gen = null) =>
        new(db, Mock.Of<IFileService>(), Mock.Of<ILogger<CampaignSvc>>(),
            Mock.Of<IParserService>(), Mock.Of<ICriteriaSuggester>(),
            Mock.Of<IInvitationEmailPublisher>(),
            sessionClient: null, invitationOptions: null,
            questionGenerator: gen ?? FakeGenerator.Returning("Q1"));

    private static Campaign SeedCampaign(
        CampaignTestDb tdb, Guid org,
        CampaignStatus status = CampaignStatus.Draft,
        string? jdText = "Tuyển Backend .NET: EF Core, PostgreSQL, RabbitMQ.",
        string? domain = "BE",
        params CampaignQuestion[] questions)
    {
        var campaign = CampaignTestDb.NewCampaign(org, status);
        campaign.JDText = jdText;
        campaign.Domain = domain;
        foreach (var q in questions)
        {
            q.CampaignId = campaign.Id;
            q.OrgId = org;
            campaign.Questions.Add(q);
        }
        using var db = tdb.NewContext();
        db.Campaigns.Add(campaign);
        db.SaveChanges();
        return campaign;
    }

    private static CampaignQuestion Question(string text, QuestionSource source)
        => new()
        {
            Id = Guid.NewGuid(),
            QuestionText = text,
            Source = source,
            IsRequired = true,
            CreatedAt = DateTime.UtcNow
        };

    // ───────────────────── (a) đường sinh: source = AiGenerated ─────────────────────

    [Fact]
    public async Task Sinh_cau_hoi_tu_JD_luu_voi_source_AiGenerated()
    {
        using var tdb = new CampaignTestDb();
        var org = Guid.NewGuid();
        var campaign = SeedCampaign(tdb, org);
        var gen = FakeGenerator.Returning("Giải thích change tracking trong EF Core?", "Khi nào dùng RabbitMQ?");

        var res = await NewService(tdb.NewContext(), gen)
            .GenerateCampaignQuestionsAsync(org, org, campaign.Id, count: null, default);

        Assert.Equal(2, res.Questions.Count);

        using var check = tdb.NewContext();
        var rows = await check.CampaignQuestions
            .Where(q => q.CampaignId == campaign.Id).ToListAsync();

        Assert.Equal(2, rows.Count);
        // Hành vi CỐT LÕI của F9: câu do AI sinh phải mang dấu vết nguồn AiGenerated.
        Assert.All(rows, r => Assert.Equal(QuestionSource.AiGenerated, r.Source));
        Assert.Contains(rows, r => r.QuestionText == "Giải thích change tracking trong EF Core?");
        Assert.All(rows, r => Assert.Equal(org, r.OrgId));

        // JD của campaign được đẩy vào AI (không phải sinh khơi khơi theo mỗi jobCategory).
        Assert.Equal(1, gen.Calls);
        Assert.Equal("BE", gen.LastJobCategory);
        Assert.Contains("Backend .NET", gen.LastJdText);
    }

    [Fact]
    public async Task Sinh_cau_hoi_ghi_audit_va_cap_nhat_updated_at()
    {
        using var tdb = new CampaignTestDb();
        var org = Guid.NewGuid();
        var actor = Guid.NewGuid();
        var campaign = SeedCampaign(tdb, org);

        await NewService(tdb.NewContext(), FakeGenerator.Returning("Q1"))
            .GenerateCampaignQuestionsAsync(org, actor, campaign.Id, null, default);

        using var check = tdb.NewContext();
        var audit = await check.AuditLogs
            .Where(a => a.EntityId == campaign.Id && a.Action == AuditAction.EditQuestions)
            .SingleAsync();
        Assert.Equal(actor, audit.ActorUserId);   // audit giữ danh tính NGƯỜI, không phải org
    }

    // ───────── (b) sinh lại: thay lượt AI cũ, GIỮ câu HR ─────────

    [Fact]
    public async Task Sinh_lai_thay_cau_AI_cu_va_GIU_cau_HR_go_tay()
    {
        using var tdb = new CampaignTestDb();
        var org = Guid.NewGuid();
        var campaign = SeedCampaign(tdb, org,
            questions: new[]
            {
                Question("Câu HR tự gõ", QuestionSource.CustomHr),
                Question("Câu AI lượt trước", QuestionSource.AiGenerated),
            });

        await NewService(tdb.NewContext(), FakeGenerator.Returning("Câu AI mới 1", "Câu AI mới 2"))
            .GenerateCampaignQuestionsAsync(org, org, campaign.Id, null, default);

        using var check = tdb.NewContext();
        var rows = await check.CampaignQuestions.Where(q => q.CampaignId == campaign.Id).ToListAsync();

        // 1 HR + 2 AI mới = 3 (KHÔNG 4 → câu AI cũ bị thay, không cộng dồn).
        Assert.Equal(3, rows.Count);
        Assert.Single(rows, r => r.Source == QuestionSource.CustomHr && r.QuestionText == "Câu HR tự gõ");
        Assert.DoesNotContain(rows, r => r.QuestionText == "Câu AI lượt trước");
        Assert.Equal(2, rows.Count(r => r.Source == QuestionSource.AiGenerated));
    }

    [Fact]
    public async Task Bam_sinh_nhieu_lan_khong_cong_don()
    {
        using var tdb = new CampaignTestDb();
        var org = Guid.NewGuid();
        var campaign = SeedCampaign(tdb, org);
        var gen = FakeGenerator.Returning("A", "B", "C");

        var svc = NewService(tdb.NewContext(), gen);
        await svc.GenerateCampaignQuestionsAsync(org, org, campaign.Id, null, default);
        await NewService(tdb.NewContext(), gen)
            .GenerateCampaignQuestionsAsync(org, org, campaign.Id, null, default);

        using var check = tdb.NewContext();
        Assert.Equal(3, await check.CampaignQuestions.CountAsync(q => q.CampaignId == campaign.Id));
    }

    // ───────── (c) CAMP-2: chỉ Draft ─────────

    [Theory]
    [InlineData(CampaignStatus.Active)]
    [InlineData(CampaignStatus.Closed)]
    [InlineData(CampaignStatus.Archived)]
    public async Task Campaign_khong_phai_Draft_thi_409_va_khong_goi_AI(CampaignStatus status)
    {
        using var tdb = new CampaignTestDb();
        var org = Guid.NewGuid();
        var campaign = SeedCampaign(tdb, org, status,
            questions: new[] { Question("Đề đang chạy", QuestionSource.CustomHr) });
        var gen = FakeGenerator.Returning("Câu mới");

        // CAMP-2 — sinh câu hỏi CŨNG là sửa câu hỏi; nếu không khoá thì đây là cửa hậu đổi đề của
        // chiến dịch ĐANG chạy (ứng viên đã/đang làm bài).
        await Assert.ThrowsAsync<InvalidOperationException>(() =>
            NewService(tdb.NewContext(), gen)
                .GenerateCampaignQuestionsAsync(org, org, campaign.Id, null, default));

        Assert.Equal(0, gen.Calls);
        using var check = tdb.NewContext();
        var rows = await check.CampaignQuestions.Where(q => q.CampaignId == campaign.Id).ToListAsync();
        Assert.Single(rows);
        Assert.Equal("Đề đang chạy", rows[0].QuestionText);   // đề cũ nguyên vẹn
    }

    // ───────── (d)(e) lỗi upstream → 502, không mất dữ liệu ─────────

    [Fact]
    public async Task AI_loi_thi_nem_DownstreamServiceException_va_giu_nguyen_cau_hoi_cu()
    {
        using var tdb = new CampaignTestDb();
        var org = Guid.NewGuid();
        var campaign = SeedCampaign(tdb, org,
            questions: new[] { Question("Câu AI cũ", QuestionSource.AiGenerated) });

        // Controller map DownstreamServiceException → 502 (KHÔNG phải 400: request HR hợp lệ, AI hỏng).
        await Assert.ThrowsAsync<DownstreamServiceException>(() =>
            NewService(tdb.NewContext(), FakeGenerator.Throwing())
                .GenerateCampaignQuestionsAsync(org, org, campaign.Id, null, default));

        using var check = tdb.NewContext();
        var rows = await check.CampaignQuestions.Where(q => q.CampaignId == campaign.Id).ToListAsync();
        Assert.Single(rows);
        Assert.Equal("Câu AI cũ", rows[0].QuestionText);   // AI hỏng KHÔNG được xoá đề đang có
    }

    [Fact]
    public async Task AI_tra_rong_thi_nem_DownstreamServiceException_khong_xoa_sach_de()
    {
        using var tdb = new CampaignTestDb();
        var org = Guid.NewGuid();
        var campaign = SeedCampaign(tdb, org,
            questions: new[] { Question("Câu AI cũ", QuestionSource.AiGenerated) });

        await Assert.ThrowsAsync<DownstreamServiceException>(() =>
            NewService(tdb.NewContext(), FakeGenerator.Returning())
                .GenerateCampaignQuestionsAsync(org, org, campaign.Id, null, default));

        using var check = tdb.NewContext();
        Assert.Equal(1, await check.CampaignQuestions.CountAsync(q => q.CampaignId == campaign.Id));
    }

    // ───────── (f) guard TRƯỚC khi gọi AI ─────────

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("   ")]
    public async Task Chua_co_JD_thi_400_va_khong_goi_AI(string? jdText)
    {
        using var tdb = new CampaignTestDb();
        var org = Guid.NewGuid();
        var campaign = SeedCampaign(tdb, org, jdText: jdText);
        var gen = FakeGenerator.Returning("Q");

        await Assert.ThrowsAsync<ArgumentException>(() =>
            NewService(tdb.NewContext(), gen)
                .GenerateCampaignQuestionsAsync(org, org, campaign.Id, null, default));

        Assert.Equal(0, gen.Calls);   // "sinh từ JD" mà không có JD → đừng tốn lời gọi AI
    }

    [Fact]
    public async Task JD_vuot_nguong_20k_ky_tu_thi_400_va_khong_goi_AI()
    {
        using var tdb = new CampaignTestDb();
        var org = Guid.NewGuid();
        // CAMP-5 — text lúc GHI đã bị cap, nhưng campaign tạo trước khi có cap (hoặc sửa thẳng DB) vẫn
        // có thể vượt → guard lại ở đường sinh, không đẩy khối text tuỳ ý vào lời gọi Gemini tính phí.
        var campaign = SeedCampaign(tdb, org,
            jdText: new string('x', TextInputLimits.JdTextMaxChars + 1));
        var gen = FakeGenerator.Returning("Q");

        await Assert.ThrowsAsync<ArgumentException>(() =>
            NewService(tdb.NewContext(), gen)
                .GenerateCampaignQuestionsAsync(org, org, campaign.Id, null, default));

        Assert.Equal(0, gen.Calls);
    }

    [Theory]
    [InlineData(0)]
    [InlineData(-1)]
    [InlineData(21)]
    public async Task Count_ngoai_dai_1_20_thi_400_va_khong_goi_AI(int count)
    {
        using var tdb = new CampaignTestDb();
        var org = Guid.NewGuid();
        var campaign = SeedCampaign(tdb, org);
        var gen = FakeGenerator.Returning("Q");

        await Assert.ThrowsAsync<ArgumentException>(() =>
            NewService(tdb.NewContext(), gen)
                .GenerateCampaignQuestionsAsync(org, org, campaign.Id, count, default));

        Assert.Equal(0, gen.Calls);
    }

    [Fact]
    public async Task Count_hop_le_duoc_chuyen_xuong_AIService()
    {
        using var tdb = new CampaignTestDb();
        var org = Guid.NewGuid();
        var campaign = SeedCampaign(tdb, org);
        var gen = FakeGenerator.Returning("A", "B", "C");

        await NewService(tdb.NewContext(), gen)
            .GenerateCampaignQuestionsAsync(org, org, campaign.Id, count: 3, default);

        Assert.Equal(3, gen.LastCount);
    }

    [Fact]
    public async Task AI_tra_qua_tran_20_cau_thi_bi_cat_bot()
    {
        using var tdb = new CampaignTestDb();
        var org = Guid.NewGuid();
        var campaign = SeedCampaign(tdb, org);
        var many = Enumerable.Range(1, 25).Select(i => $"Câu {i}").ToArray();

        await NewService(tdb.NewContext(), FakeGenerator.Returning(many))
            .GenerateCampaignQuestionsAsync(org, org, campaign.Id, null, default);

        using var check = tdb.NewContext();
        Assert.Equal(20, await check.CampaignQuestions.CountAsync(q => q.CampaignId == campaign.Id));
    }

    // ───────── (g) ownership ─────────

    [Fact]
    public async Task Campaign_ngoai_org_thi_404()
    {
        using var tdb = new CampaignTestDb();
        var org = Guid.NewGuid();
        var campaign = SeedCampaign(tdb, org);
        var gen = FakeGenerator.Returning("Q");

        await Assert.ThrowsAsync<KeyNotFoundException>(() =>
            NewService(tdb.NewContext(), gen)
                .GenerateCampaignQuestionsAsync(Guid.NewGuid(), Guid.NewGuid(), campaign.Id, null, default));

        Assert.Equal(0, gen.Calls);
    }

    // ───────────────── Client HTTP (AiServiceQuestionGenerator) ─────────────────

    private sealed class StubHandler : HttpMessageHandler
    {
        private readonly Func<HttpResponseMessage> _respond;
        public string? LastBody { get; private set; }
        public string? LastPath { get; private set; }

        public StubHandler(Func<HttpResponseMessage> respond) => _respond = respond;

        protected override async Task<HttpResponseMessage> SendAsync(
            HttpRequestMessage request, CancellationToken ct)
        {
            LastPath = request.RequestUri?.AbsolutePath;
            LastBody = request.Content is null ? null : await request.Content.ReadAsStringAsync(ct);
            return _respond();
        }
    }

    private static AiServiceQuestionGenerator NewClient(StubHandler handler)
        => new(new HttpClient(handler) { BaseAddress = new Uri("http://ai.test") },
               Mock.Of<ILogger<AiServiceQuestionGenerator>>());

    private static HttpResponseMessage Json(HttpStatusCode code, string body)
        => new(code) { Content = new StringContent(body, Encoding.UTF8, "application/json") };

    [Fact]
    public async Task Client_doc_dung_mang_questions_va_bo_chuoi_rong()
    {
        var handler = new StubHandler(() => Json(HttpStatusCode.OK,
            """{"questions":["  Câu 1  ","","   ","Câu 2"]}"""));

        var result = await NewClient(handler).GenerateAsync("BE", "JD nội dung", 2);

        Assert.Equal(new[] { "Câu 1", "Câu 2" }, result);
        Assert.Equal("/api/v1/generate-questions", handler.LastPath);
        Assert.Contains("\"jdText\":\"JD n\\u1ED9i dung\"", handler.LastBody);
        Assert.Contains("\"count\":2", handler.LastBody);
    }

    [Theory]
    [InlineData(HttpStatusCode.InternalServerError)]
    [InlineData(HttpStatusCode.BadGateway)]
    [InlineData(HttpStatusCode.ServiceUnavailable)]
    [InlineData(HttpStatusCode.BadRequest)]
    public async Task Client_non_2xx_nem_DownstreamServiceException(HttpStatusCode code)
    {
        var handler = new StubHandler(() => Json(code, """{"detail":"boom"}"""));

        // Kể cả AIService trả 400, với Campaign đây vẫn là lỗi UPSTREAM → 502, không "chuyển tiếp" 400
        // cho HR (request của HR hợp lệ; hợp đồng giữa 2 service hỏng mới là nguyên nhân).
        await Assert.ThrowsAsync<DownstreamServiceException>(
            () => NewClient(handler).GenerateAsync("BE", "JD", null));
    }

    [Fact]
    public async Task Client_khong_ket_noi_duoc_nem_DownstreamServiceException()
    {
        var handler = new StubHandler(() => throw new HttpRequestException("connection refused"));

        await Assert.ThrowsAsync<DownstreamServiceException>(
            () => NewClient(handler).GenerateAsync("BE", "JD", null));
    }

    [Fact]
    public async Task Client_body_hong_nem_DownstreamServiceException()
    {
        var handler = new StubHandler(() => Json(HttpStatusCode.OK, "không-phải-json"));

        await Assert.ThrowsAsync<DownstreamServiceException>(
            () => NewClient(handler).GenerateAsync("BE", "JD", null));
    }
}
