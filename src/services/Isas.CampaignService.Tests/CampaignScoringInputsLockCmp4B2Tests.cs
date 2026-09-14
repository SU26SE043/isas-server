using Isas.CampaignService.DTOs;
using Isas.CampaignService.Models;
using Isas.CampaignService.Services;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Logging;
using Moq;

using CampaignSvc = Isas.CampaignService.Services.CampaignService;

namespace Isas.CampaignService.Tests;

/// <summary>
/// CMP4-B2 — <c>Domain</c> và <c>Language</c> (trên <c>PUT /campaign/{id}</c>) bị khoá bởi CÙNG
/// điều kiện với ba luật lọc cứng (CMP3-B2) và với <c>PUT /job-needs</c> (CMP4-B1): campaign đã
/// <c>Closed</c>/<c>Archived</c> HOẶC đã có bất kỳ <c>cv_submission</c> nào → <b>409</b>.
///
/// <para>Trước CMP4-B2: <c>Domain</c> KHÔNG có cửa nào (đổi được cả khi Active đã sàng xong);
/// <c>Language</c> chỉ 409 khi <c>≠ Draft</c> — mà CMP3-B2 cho Draft có <c>cv_submission</c> nên
/// giả định "Draft = chưa ai bị đo" đã đổ. Cả hai đi thẳng vào job sàng CV (batch / rescreen /
/// republisher) ⇒ ứng viên A và B cùng bảng xếp hạng nhưng AI chấm với domain/language khác nhau,
/// KHÔNG có nhãn phiên bản (khác <c>rubric_version</c>) ⇒ HR không nhận ra được.</para>
///
/// <para>⚠ Ghi nhận: <c>Language</c> nay NỚI cho ca <c>Active + 0 cv_submission</c> (409 → 200) —
/// đúng nguyên tắc "một điều kiện khoá mọi trường quyết định cách AI chấm", nhưng câu hỏi B2B sinh
/// lúc publish theo ngôn ngữ gốc và bị khoá trên Active (CAMP-18) nên có thể lệch với criteria/
/// screening — cần cân nhắc lúc review.</para>
/// </summary>
public class CampaignScoringInputsLockCmp4B2Tests
{
    private static IConfiguration Config(bool bilingual) =>
        new ConfigurationBuilder().AddInMemoryCollection(new Dictionary<string, string?>
        {
            ["Campaign:Bilingual:Enabled"] = bilingual ? "true" : "false"
        }).Build();

    private static IEntitlementClient Entitlements()
    {
        var client = new Mock<IEntitlementClient>();
        client.Setup(x => x.ResolveOrgAsync(It.IsAny<Guid>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(new CampaignEntitlement("test", "business", 1, 10, 200, true, true, true));
        return client.Object;
    }

    private static CampaignSvc NewSvc(CampaignDbContext db, bool bilingual = true) =>
        new(db, Mock.Of<IFileService>(), Mock.Of<ILogger<CampaignSvc>>(), Mock.Of<IParserService>(),
            Mock.Of<ICriteriaSuggester>(), Mock.Of<IInvitationEmailPublisher>(),
            entitlements: Entitlements(), config: Config(bilingual));

    private static Guid Seed(CampaignTestDb tdb, Guid owner, CampaignStatus status)
    {
        var camp = CampaignTestDb.NewCampaign(owner, status);
        camp.Domain = "BE";
        camp.Language = "vi";
        tdb.Db.Campaigns.Add(camp);
        tdb.Db.SaveChanges();
        return camp.Id;
    }

    // Ứng viên CHƯA sàng (OverallMatchScore null) — để chứng minh thước là "bất kỳ cv_submission nào",
    // KHÔNG phải "đã có điểm".
    private static async Task AddCvAsync(CampaignTestDb tdb, Guid campId)
    {
        using var c = tdb.NewContext();
        c.CvSubmissions.Add(new CvSubmission
        {
            Id = Guid.NewGuid(),
            CampaignId = campId,
            Email = $"c{Guid.NewGuid():N}@x.com",
            CvParsedText = "CV text",
            ParseStatus = CvParseStatus.Done,
            Status = CvSubmissionStatus.Filtered,
            OverallMatchScore = null,
            CreatedAt = DateTime.UtcNow,
            UpdatedAt = DateTime.UtcNow,
        });
        await c.SaveChangesAsync();
    }

    private static UpdateCampaignRequest DomainReq() => new() { Domain = "FE" };
    private static UpdateCampaignRequest LanguageReq() => new() { Language = "en" };

    // ── (1) Draft + đã có cv_submission ⇒ đổi domain 409 · đổi language 409 ────────────────────────
    [Fact]
    public async Task Draft_da_co_cv_submission_thi_doi_domain_409()
    {
        using var tdb = new CampaignTestDb();
        var owner = Guid.NewGuid();
        var campId = Seed(tdb, owner, CampaignStatus.Draft);
        await AddCvAsync(tdb, campId);

        var ex = await Assert.ThrowsAsync<InvalidOperationException>(() =>
            NewSvc(tdb.NewContext()).UpdateCampaignAsync(owner, owner, campId, DomainReq(), default));
        Assert.Contains("domain", ex.Message, StringComparison.OrdinalIgnoreCase);

        Assert.Equal("BE", tdb.NewContext().Campaigns.Single(c => c.Id == campId).Domain);
    }

    [Fact]
    public async Task Draft_da_co_cv_submission_thi_doi_language_409()
    {
        using var tdb = new CampaignTestDb();
        var owner = Guid.NewGuid();
        var campId = Seed(tdb, owner, CampaignStatus.Draft);
        await AddCvAsync(tdb, campId);

        var ex = await Assert.ThrowsAsync<InvalidOperationException>(() =>
            NewSvc(tdb.NewContext()).UpdateCampaignAsync(owner, owner, campId, LanguageReq(), default));
        Assert.Contains("language", ex.Message, StringComparison.OrdinalIgnoreCase);

        Assert.Equal("vi", tdb.NewContext().Campaigns.Single(c => c.Id == campId).Language);
    }

    // ── (2) Draft chưa có ai ⇒ cả hai 200 ────────────────────────────────────────────────────────
    [Fact]
    public async Task Draft_chua_co_ai_thi_doi_domain_va_language_200()
    {
        using var tdb = new CampaignTestDb();
        var owner = Guid.NewGuid();
        var campId = Seed(tdb, owner, CampaignStatus.Draft);

        var r1 = await NewSvc(tdb.NewContext()).UpdateCampaignAsync(owner, owner, campId, DomainReq(), default);
        Assert.Equal("FE", r1.Domain);

        var r2 = await NewSvc(tdb.NewContext()).UpdateCampaignAsync(owner, owner, campId, LanguageReq(), default);
        Assert.Equal("en", r2.Language);

        var saved = tdb.NewContext().Campaigns.Single(c => c.Id == campId);
        Assert.Equal("FE", saved.Domain);
        Assert.Equal("en", saved.Language);
    }

    // ── (3) Active chưa có ai ⇒ cả hai 200 (Language nới từ "Draft-only" — xem <summary>) ─────────
    [Fact]
    public async Task Active_chua_co_ai_thi_doi_domain_va_language_200()
    {
        using var tdb = new CampaignTestDb();
        var owner = Guid.NewGuid();
        var campId = Seed(tdb, owner, CampaignStatus.Active);

        var r1 = await NewSvc(tdb.NewContext()).UpdateCampaignAsync(owner, owner, campId, DomainReq(), default);
        Assert.Equal("FE", r1.Domain);

        var r2 = await NewSvc(tdb.NewContext()).UpdateCampaignAsync(owner, owner, campId, LanguageReq(), default);
        Assert.Equal("en", r2.Language);
    }

    // ── (4) Active + đã có cv_submission ⇒ cả hai 409 ────────────────────────────────────────────
    // Regression cho Domain: TRƯỚC CMP4-B2 nó KHÔNG có cửa nào — đổi được cả khi Active đã sàng.
    [Fact]
    public async Task Active_da_co_cv_submission_thi_doi_domain_409()
    {
        using var tdb = new CampaignTestDb();
        var owner = Guid.NewGuid();
        var campId = Seed(tdb, owner, CampaignStatus.Active);
        await AddCvAsync(tdb, campId);

        await Assert.ThrowsAsync<InvalidOperationException>(() =>
            NewSvc(tdb.NewContext()).UpdateCampaignAsync(owner, owner, campId, DomainReq(), default));

        Assert.Equal("BE", tdb.NewContext().Campaigns.Single(c => c.Id == campId).Domain);
    }

    [Fact]
    public async Task Active_da_co_cv_submission_thi_doi_language_409()
    {
        using var tdb = new CampaignTestDb();
        var owner = Guid.NewGuid();
        var campId = Seed(tdb, owner, CampaignStatus.Active);
        await AddCvAsync(tdb, campId);

        await Assert.ThrowsAsync<InvalidOperationException>(() =>
            NewSvc(tdb.NewContext()).UpdateCampaignAsync(owner, owner, campId, LanguageReq(), default));
    }

    // ── (5) Closed / Archived ⇒ cả hai 409 kể cả khi chưa có ai (thước đo là dữ liệu lịch sử) ─────
    [Theory]
    [InlineData(CampaignStatus.Closed)]
    [InlineData(CampaignStatus.Archived)]
    public async Task Terminal_chua_co_ai_thi_doi_domain_va_language_409(CampaignStatus status)
    {
        using var tdb = new CampaignTestDb();
        var owner = Guid.NewGuid();
        var campId = Seed(tdb, owner, status);

        var exD = await Assert.ThrowsAsync<InvalidOperationException>(() =>
            NewSvc(tdb.NewContext()).UpdateCampaignAsync(owner, owner, campId, DomainReq(), default));
        Assert.Contains(status.ToString(), exD.Message);

        await Assert.ThrowsAsync<InvalidOperationException>(() =>
            NewSvc(tdb.NewContext()).UpdateCampaignAsync(owner, owner, campId, LanguageReq(), default));
    }

    // ── (6) ValidateLanguage vẫn chạy: Active chưa có ai + language sai thang ⇒ 400 (không 409/200) ─
    [Fact]
    public async Task Active_chua_co_ai_language_sai_thang_thi_400()
    {
        using var tdb = new CampaignTestDb();
        var owner = Guid.NewGuid();
        var campId = Seed(tdb, owner, CampaignStatus.Active);

        await Assert.ThrowsAsync<ArgumentException>(() =>
            NewSvc(tdb.NewContext()).UpdateCampaignAsync(owner, owner, campId,
                new UpdateCampaignRequest { Language = "jp" }, default));

        Assert.Equal("vi", tdb.NewContext().Campaigns.Single(c => c.Id == campId).Language);
    }

    // ── (7) request KHÔNG chạm domain/language ⇒ khối guard KHÔNG chạy (đổi Title trên Active + CV OK) ─
    [Fact]
    public async Task Khong_cham_domain_language_thi_guard_khong_chan_field_khac()
    {
        using var tdb = new CampaignTestDb();
        var owner = Guid.NewGuid();
        var campId = Seed(tdb, owner, CampaignStatus.Active);
        await AddCvAsync(tdb, campId);

        var res = await NewSvc(tdb.NewContext()).UpdateCampaignAsync(owner, owner, campId,
            new UpdateCampaignRequest { Title = "Tiêu đề mới" }, default);

        Assert.Equal("Tiêu đề mới", res.Title);
    }

    // ── (8) SCR1-review: ECHO giá trị Y HỆT ⇒ KHÔNG khoá — FE wizard gửi `domain` ở MỌI lần lưu ──────
    // Đo trên dev 14/09: sau khi SCR1 cho Draft sàng CV, wizard PUT {title, domain:"Backend"} (không đổi
    // gì) → 409 ⇒ HR sàng xong 1 CV là không bao giờ bấm Triển khai được nữa. Guard chỉ được chặn khi
    // giá trị THẬT SỰ ĐỔI (cùng luật với PassScorePct trong chính method này).
    [Fact]
    public async Task Draft_da_co_cv_echo_domain_language_giong_het_thi_200()
    {
        using var tdb = new CampaignTestDb();
        var owner = Guid.NewGuid();
        var campId = Seed(tdb, owner, CampaignStatus.Draft);
        await AddCvAsync(tdb, campId);

        var res = await NewSvc(tdb.NewContext()).UpdateCampaignAsync(owner, owner, campId,
            new UpdateCampaignRequest { Title = "Vẫn lưu được", Domain = "BE", Language = "vi" }, default);

        Assert.Equal("Vẫn lưu được", res.Title);
        var saved = tdb.NewContext().Campaigns.Single(c => c.Id == campId);
        Assert.Equal("BE", saved.Domain);
        Assert.Equal("vi", saved.Language);
    }

    // ── (9) Luật lọc echo lại BẢN ĐANG LƯU (kể cả khác thứ tự trắng/rỗng sau chuẩn hoá) ⇒ 200; đổi thật ⇒ 409 ─
    [Fact]
    public async Task Draft_da_co_cv_echo_luat_loc_giong_het_thi_200_doi_that_thi_409()
    {
        using var tdb = new CampaignTestDb();
        var owner = Guid.NewGuid();
        var campId = Seed(tdb, owner, CampaignStatus.Draft);
        using (var c = tdb.NewContext())
        {
            var camp = c.Campaigns.Single(x => x.Id == campId);
            camp.RequiredSkills = new List<string> { "Java", "Spring" };
            camp.MinYearsExperience = null;
            c.SaveChanges();
        }
        await AddCvAsync(tdb, campId);

        // Echo: cùng danh sách (có khoảng trắng thừa — Clean() trim), keywords rỗng (= null đang lưu),
        // minYears 0 (≡ null: hard-filter chỉ áp khi > 0).
        var ok = await NewSvc(tdb.NewContext()).UpdateCampaignAsync(owner, owner, campId,
            new UpdateCampaignRequest
            {
                Title = "Echo",
                RequiredSkills = new List<string> { " Java", "Spring " },
                KeywordsAny = new List<string>(),
                MinYearsExperience = 0,
            }, default);
        Assert.Equal("Echo", ok.Title);

        // Đổi THẬT một luật ⇒ vẫn 409 và không ghi.
        var ex = await Assert.ThrowsAsync<InvalidOperationException>(() =>
            NewSvc(tdb.NewContext()).UpdateCampaignAsync(owner, owner, campId,
                new UpdateCampaignRequest { RequiredSkills = new List<string> { "Java", "Spring", "Kafka" } }, default));
        Assert.Contains("đã có ứng viên", ex.Message);
        Assert.Equal(new[] { "Java", "Spring" },
            tdb.NewContext().Campaigns.Single(c => c.Id == campId).RequiredSkills);
    }

    // ── (10) Echo trên Closed cũng KHÔNG 409 (không có gì đổi) nhưng đổi thật thì 409 như cũ ──────
    [Fact]
    public async Task Closed_echo_domain_giong_het_thi_200_doi_that_thi_409()
    {
        using var tdb = new CampaignTestDb();
        var owner = Guid.NewGuid();
        var campId = Seed(tdb, owner, CampaignStatus.Closed);

        var ok = await NewSvc(tdb.NewContext()).UpdateCampaignAsync(owner, owner, campId,
            new UpdateCampaignRequest { Title = "Echo closed", Domain = "BE" }, default);
        Assert.Equal("Echo closed", ok.Title);

        await Assert.ThrowsAsync<InvalidOperationException>(() =>
            NewSvc(tdb.NewContext()).UpdateCampaignAsync(owner, owner, campId, DomainReq(), default));
        Assert.Equal("BE", tdb.NewContext().Campaigns.Single(c => c.Id == campId).Domain);
    }

    // ── (11) Chuỗi rỗng vẫn 400 (BK35) kể cả khi campaign đã có ứng viên — validate chạy TRƯỚC khi so ─
    [Fact]
    public async Task Draft_da_co_cv_language_rong_thi_400_khong_phai_409()
    {
        using var tdb = new CampaignTestDb();
        var owner = Guid.NewGuid();
        var campId = Seed(tdb, owner, CampaignStatus.Draft);
        await AddCvAsync(tdb, campId);

        await Assert.ThrowsAsync<ArgumentException>(() =>
            NewSvc(tdb.NewContext()).UpdateCampaignAsync(owner, owner, campId,
                new UpdateCampaignRequest { Language = "" }, default));
        Assert.Equal("vi", tdb.NewContext().Campaigns.Single(c => c.Id == campId).Language);
    }
}
