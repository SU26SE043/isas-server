using Isas.CampaignService.DTOs;
using Isas.CampaignService.Models;
using Isas.CampaignService.Services;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging;
using Moq;

using CampaignSvc = Isas.CampaignService.Services.CampaignService;

namespace Isas.CampaignService.Tests;

/// <summary>
/// CMP3-B3 — <c>POST /campaign/{id}/job-needs/suggest</c>: AI đọc JD → đề xuất nhu cầu công việc
/// để HR chốt (qua PUT /job-needs) TRƯỚC khi sàng CV. CHỈ ĐỌC — endpoint KHÔNG ghi
/// <c>campaigns.job_needs</c>.
///
/// <para>Sau CMP3-B1 sàng CV đòi <c>job_needs</c>; <c>job_needs</c> hôm nay chỉ sinh lúc publish
/// nên ở Draft luôn rỗng ⇒ HR không có đường chốt thước trước. Endpoint này là nút "AI gợi ý"
/// cho luồng Draft.</para>
/// </summary>
public class CampaignSuggestJobNeedsCmp3B3Tests
{
    private static CampaignSvc NewSvc(CampaignDbContext db, IJobNeedsSuggester? suggester)
        => new(db, Mock.Of<IFileService>(), Mock.Of<ILogger<CampaignSvc>>(), Mock.Of<IParserService>(),
            Mock.Of<ICriteriaSuggester>(), Mock.Of<IInvitationEmailPublisher>(),
            jobNeedsSuggester: suggester);

    // Bộ gợi ý trả đúng `needs` truyền vào; null ⇒ mô phỏng AIService lỗi (AiServiceJobNeedsSuggester
    // nuốt exception / non-2xx → null).
    private static IJobNeedsSuggester Suggester(params SuggestedJobNeed[]? needs)
    {
        var m = new Mock<IJobNeedsSuggester>();
        m.Setup(s => s.SuggestAsync(It.IsAny<string>(), It.IsAny<string?>(), It.IsAny<string>(),
                                    It.IsAny<CancellationToken>()))
         .ReturnsAsync(needs?.ToList());
        return m.Object;
    }

    private static Guid SeedDraft(CampaignTestDb tdb, Guid owner, string? jd = "JD: cần Backend .NET 3 năm")
    {
        var camp = CampaignTestDb.NewCampaign(owner, CampaignStatus.Draft);
        camp.Domain = "BE";
        camp.JDText = jd;
        tdb.Db.Campaigns.Add(camp);
        tdb.Db.SaveChanges();
        return camp.Id;
    }

    // ── (1) Mọi dòng trả về: source = "AiSuggested", isMustHave = false ────────────────────
    [Fact]
    public async Task Moi_dong_co_source_AiSuggested_va_isMustHave_false()
    {
        using var tdb = new CampaignTestDb();
        var owner = Guid.NewGuid();
        var campId = SeedDraft(tdb, owner);

        var res = await NewSvc(tdb.NewContext(), Suggester(
            new SuggestedJobNeed(JobNeedCategories.Technical, "Thạo .NET"),
            new SuggestedJobNeed(JobNeedCategories.Communication, "Trình bày rõ ràng"))
        ).SuggestJobNeedsAsync(owner, campId, default);

        Assert.Equal(2, res.JobNeeds.Count);
        Assert.All(res.JobNeeds, n => Assert.Equal("AiSuggested", n.Source));
        Assert.All(res.JobNeeds, n => Assert.False(n.IsMustHave));
        Assert.Equal("Thạo .NET", res.JobNeeds[0].Text);
        Assert.Equal(JobNeedCategories.Technical, res.JobNeeds[0].Category);
    }

    // ── (2) Gọi HAI LẦN — DB không đổi (job_needs, số row, UpdatedAt) ──────────────────────
    [Fact]
    public async Task Goi_hai_lan_DB_khong_doi()
    {
        using var tdb = new CampaignTestDb();
        var owner = Guid.NewGuid();
        var campId = SeedDraft(tdb, owner);

        var before = await tdb.NewContext().Campaigns.AsNoTracking().SingleAsync(c => c.Id == campId);

        var s = Suggester(new SuggestedJobNeed(JobNeedCategories.Technical, "Thạo .NET"));
        await NewSvc(tdb.NewContext(), s).SuggestJobNeedsAsync(owner, campId, default);
        await NewSvc(tdb.NewContext(), s).SuggestJobNeedsAsync(owner, campId, default);

        using var check = tdb.NewContext();
        var after = await check.Campaigns.AsNoTracking().SingleAsync(c => c.Id == campId);
        Assert.Null(after.JobNeeds);                               // vẫn CHƯA có job_needs
        Assert.Equal(before.UpdatedAt, after.UpdatedAt);           // không đường nào ghi
        Assert.Equal(0, await check.CvSubmissions.CountAsync(c => c.CampaignId == campId));
    }

    // ── (3) jdText rỗng ⇒ ArgumentException (controller → 400), nói rõ thiếu JD ────────────
    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("   ")]
    public async Task JdText_rong_thi_400(string? jd)
    {
        using var tdb = new CampaignTestDb();
        var owner = Guid.NewGuid();
        var campId = SeedDraft(tdb, owner, jd: jd);

        var ex = await Assert.ThrowsAsync<ArgumentException>(() =>
            NewSvc(tdb.NewContext(), Suggester(new SuggestedJobNeed(JobNeedCategories.Technical, "x")))
                .SuggestJobNeedsAsync(owner, campId, default));

        Assert.Contains("jdText", ex.Message);
        // Bộ gợi ý KHÔNG được gọi khi thiếu JD.
        Assert.Null(await tdb.NewContext().Campaigns.AsNoTracking()
            .Where(c => c.Id == campId).Select(c => c.JobNeeds).SingleAsync());
    }

    // ── (4) AIService lỗi (SuggestAsync → null) ⇒ DownstreamServiceException (→ 502), DB nguyên ─
    [Fact]
    public async Task AIService_loi_thi_502_va_DB_khong_doi()
    {
        using var tdb = new CampaignTestDb();
        var owner = Guid.NewGuid();
        var campId = SeedDraft(tdb, owner);

        await Assert.ThrowsAsync<DownstreamServiceException>(() =>
            NewSvc(tdb.NewContext(), Suggester(null)).SuggestJobNeedsAsync(owner, campId, default));

        using var check = tdb.NewContext();
        Assert.Null(await check.Campaigns.AsNoTracking()
            .Where(c => c.Id == campId).Select(c => c.JobNeeds).SingleAsync());
    }

    // ── (5) Ngoài org ⇒ KeyNotFoundException (→ 404) ──────────────────────────────────────
    [Fact]
    public async Task Ngoai_org_thi_404()
    {
        using var tdb = new CampaignTestDb();
        var owner = Guid.NewGuid();
        var campId = SeedDraft(tdb, owner);

        await Assert.ThrowsAsync<KeyNotFoundException>(() =>
            NewSvc(tdb.NewContext(), Suggester(new SuggestedJobNeed(JobNeedCategories.Technical, "x")))
                .SuggestJobNeedsAsync(Guid.NewGuid() /* org khác */, campId, default));
    }

    // ── (6) AI 2xx nhưng 0 dòng hợp lệ ⇒ 200 với jobNeeds rỗng (không phải lỗi) ────────────
    [Fact]
    public async Task AI_tra_rong_thi_200_jobNeeds_rong()
    {
        using var tdb = new CampaignTestDb();
        var owner = Guid.NewGuid();
        var campId = SeedDraft(tdb, owner);

        var res = await NewSvc(tdb.NewContext(), Suggester(Array.Empty<SuggestedJobNeed>()))
            .SuggestJobNeedsAsync(owner, campId, default);

        Assert.Empty(res.JobNeeds);
    }
}
