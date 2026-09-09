using Isas.CampaignService.DTOs;
using Isas.CampaignService.Models;
using Isas.CampaignService.Services;
using Microsoft.AspNetCore.Http;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging;
using Moq;
using System.Text;

using CampaignSvc = Isas.CampaignService.Services.CampaignService;

namespace Isas.CampaignService.Tests;

/// <summary>
/// CMP3-B2 — nhận + sàng CV được khi campaign còn <c>Draft</c> (guard cũ đòi <c>Active</c> với lý do
/// "đã có campaign_criteria" — SAI từ CAMP-14: sàng CV đo bằng <c>job_needs</c>, hard-filter chỉ đọc
/// RequiredSkills/KeywordsAny/MinYearsExperience, ba trường sửa được ở Draft).
///
/// <para>Mở Draft phá giả định "Draft không thể có ứng viên" mà hai cửa dựa vào ⇒ siết cùng PR:</para>
/// <list type="bullet">
///   <item><c>ReplaceJobNeedsAsync</c>: cửa sửa nay là "CHƯA có ứng viên nào được sàng"
///     (<c>OverallMatchScore != null</c>), bất kể trạng thái. Closed/Archived vẫn 409.</item>
///   <item>Khối luật lọc cứng trong <c>UpdateCampaignAsync</c>: cửa sửa nay là "CHƯA có
///     <c>cv_submission</c> nào", bất kể trạng thái. Closed/Archived vẫn 409.</item>
/// </list>
/// </summary>
public class CampaignScreenAtDraftCmp3B2Tests
{
    private static CampaignSvc NewSvc(CampaignDbContext db, params string[] parsedTexts)
    {
        var parser = new Mock<IParserService>();
        var seq = parser.SetupSequence(p => p.ParseAsync(It.IsAny<Stream>(), It.IsAny<CancellationToken>()));
        foreach (var t in parsedTexts.Length == 0 ? new[] { "cv text cand@x.com" } : parsedTexts)
            seq = seq.ReturnsAsync(new ParseResult { RawText = t });

        var ent = new Mock<IEntitlementClient>();
        ent.Setup(x => x.ResolveOrgAsync(It.IsAny<Guid>(), It.IsAny<CancellationToken>()))
           .ReturnsAsync(new CampaignEntitlement("test", "business", 1, 10, 200, true, true, true));

        return new CampaignSvc(db, new Mock<IFileService>().Object,
            Mock.Of<ILogger<CampaignSvc>>(), parser.Object,
            Mock.Of<ICriteriaSuggester>(), Mock.Of<IInvitationEmailPublisher>(),
            entitlements: ent.Object);
    }

    private static IFormFile Pdf(string fileName = "cv.pdf")
    {
        var s = new MemoryStream(Encoding.UTF8.GetBytes("xxxxxxxx"));
        return new FormFile(s, 0, s.Length, "files", fileName)
        { Headers = new HeaderDictionary(), ContentType = "application/pdf" };
    }

    private static IFormFileCollection Files(params IFormFile[] f)
    {
        var c = new FormFileCollection(); c.AddRange(f); return c;
    }

    private static List<JobNeed> OneNeed() => new()
    {
        new() { NeedId = "n1", Category = JobNeedCategories.Technical, Text = "Thạo .NET", Source = JobNeedSources.HrEdited },
    };

    private static Guid Seed(CampaignTestDb tdb, Guid owner,
        CampaignStatus status = CampaignStatus.Draft, bool withJobNeeds = true)
    {
        var camp = CampaignTestDb.NewCampaign(owner, status);
        camp.Domain = "BE";
        if (withJobNeeds) camp.JobNeeds = OneNeed();
        tdb.Db.Campaigns.Add(camp);
        tdb.Db.SaveChanges();
        return camp.Id;
    }

    private static async Task AddCvAsync(CampaignTestDb tdb, Guid campId, int? overallMatchScore)
    {
        using var c = tdb.NewContext();
        c.CvSubmissions.Add(new CvSubmission
        {
            Id = Guid.NewGuid(),
            CampaignId = campId,
            Email = $"c{Guid.NewGuid():N}@x.com",
            ParseStatus = CvParseStatus.Done,
            Status = overallMatchScore is null ? CvSubmissionStatus.Filtered : CvSubmissionStatus.Analyzed,
            OverallMatchScore = overallMatchScore,
            CreatedAt = DateTime.UtcNow,
            UpdatedAt = DateTime.UtcNow,
        });
        await c.SaveChangesAsync();
    }

    // ── (1) Draft + có job_needs ⇒ sàng được, sinh row Filtered ─────────────────────────────
    [Fact]
    public async Task Draft_co_job_needs_thi_sang_duoc_sinh_row()
    {
        using var tdb = new CampaignTestDb();
        var owner = Guid.NewGuid();
        var campId = Seed(tdb, owner, CampaignStatus.Draft);

        var res = await NewSvc(tdb.NewContext(), "kinh nghiem .NET cand@x.com")
            .ScreenCandidatesAsync(owner, owner, campId, Files(Pdf()), default);

        Assert.Equal(1, res.Filtered);
        using var check = tdb.NewContext();
        var row = await check.CvSubmissions.SingleAsync(c => c.CampaignId == campId);
        Assert.Equal(CvSubmissionStatus.Filtered, row.Status);
    }

    // ── (2) Closed / Archived vẫn 409, không sinh row ──────────────────────────────────────
    [Theory]
    [InlineData(CampaignStatus.Closed)]
    [InlineData(CampaignStatus.Archived)]
    public async Task Screen_Closed_Archived_van_409(CampaignStatus status)
    {
        using var tdb = new CampaignTestDb();
        var owner = Guid.NewGuid();
        var campId = Seed(tdb, owner, status);

        await Assert.ThrowsAsync<InvalidOperationException>(() =>
            NewSvc(tdb.NewContext()).ScreenCandidatesAsync(owner, owner, campId, Files(Pdf()), default));

        Assert.Equal(0, await tdb.NewContext().CvSubmissions.CountAsync(c => c.CampaignId == campId));
    }

    // ── (3) Draft đã có 1 ứng viên ĐÃ SÀNG (OverallMatchScore != null) ⇒ PUT /job-needs 409 ──
    [Fact]
    public async Task Draft_da_co_ung_vien_da_sang_thi_ReplaceJobNeeds_409()
    {
        using var tdb = new CampaignTestDb();
        var owner = Guid.NewGuid();
        var campId = Seed(tdb, owner, CampaignStatus.Draft);
        await AddCvAsync(tdb, campId, overallMatchScore: 80);

        var ex = await Assert.ThrowsAsync<InvalidOperationException>(() =>
            NewSvc(tdb.NewContext()).ReplaceJobNeedsAsync(owner, owner, campId, new List<JobNeedInput>
            {
                new() { Category = JobNeedCategories.Technical, Text = "Thạo Kafka" },
            }, default));

        Assert.Contains("được sàng", ex.Message);
    }

    // ── (4) Draft đã có cv_submission (CHƯA sàng, OverallMatchScore null) ⇒ PUT /campaign đổi
    //        requiredSkills 409. Ngưỡng KHÁC (3): bất kỳ row CV nào, không đòi đã có điểm.
    [Fact]
    public async Task Draft_da_co_cv_submission_thi_UpdateCampaign_doi_requiredSkills_409()
    {
        using var tdb = new CampaignTestDb();
        var owner = Guid.NewGuid();
        var campId = Seed(tdb, owner, CampaignStatus.Draft);
        await AddCvAsync(tdb, campId, overallMatchScore: null);   // chưa sàng

        await Assert.ThrowsAsync<InvalidOperationException>(() =>
            NewSvc(tdb.NewContext()).UpdateCampaignAsync(owner, owner, campId,
                new UpdateCampaignRequest { RequiredSkills = new() { "Kubernetes" } }, default));
    }

    // ── (5) Draft CHƯA có ứng viên ⇒ cả hai PUT vẫn 200 ───────────────────────────────────
    [Fact]
    public async Task Draft_chua_co_ung_vien_thi_ca_hai_PUT_van_200()
    {
        using var tdb = new CampaignTestDb();
        var owner = Guid.NewGuid();
        var campId = Seed(tdb, owner, CampaignStatus.Draft);

        var afterNeeds = await NewSvc(tdb.NewContext()).ReplaceJobNeedsAsync(owner, owner, campId,
            new List<JobNeedInput> { new() { Category = JobNeedCategories.Technical, Text = "Thạo Go" } }, default);
        Assert.Single(afterNeeds.JobNeeds);

        var afterFilter = await NewSvc(tdb.NewContext()).UpdateCampaignAsync(owner, owner, campId,
            new UpdateCampaignRequest { RequiredSkills = new() { "Kubernetes" } }, default);
        Assert.Equal(new List<string> { "Kubernetes" }, afterFilter.RequiredSkills);
    }

    // ── (regression) CMP1-B2 vẫn đứng: Active đã có người sàng ⇒ ReplaceJobNeeds 409 ────────
    [Fact]
    public async Task Active_da_co_ung_vien_da_sang_thi_ReplaceJobNeeds_409()
    {
        using var tdb = new CampaignTestDb();
        var owner = Guid.NewGuid();
        var campId = Seed(tdb, owner, CampaignStatus.Active);
        await AddCvAsync(tdb, campId, overallMatchScore: 55);

        await Assert.ThrowsAsync<InvalidOperationException>(() =>
            NewSvc(tdb.NewContext()).ReplaceJobNeedsAsync(owner, owner, campId, new List<JobNeedInput>
            {
                new() { Category = JobNeedCategories.Technical, Text = "x" },
            }, default));
    }
}
