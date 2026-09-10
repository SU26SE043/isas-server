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
/// CMP3-B1 — <c>ScreenCandidatesAsync</c> phải CHẶN NGAY (409) khi campaign chưa chốt
/// <c>job_needs</c>, TRƯỚC khi tạo bất kỳ row <c>cv_submission</c> nào.
///
/// <para>Vì sao đây là bug: khi job_needs rỗng, sàng CV hiện đi TRỌN đường — controller trả 202,
/// row nằm <c>Filtered</c> marker null, <c>StuckScreeningRepublisher</c> nhặt nhưng trần bỏ cuộc
/// chạy TRƯỚC phép kiểm job_needs của nó ⇒ sau ~6 giờ mọi ứng viên lật <c>AnalysisFailed</c> với
/// lý do "kiểm tra consumer" trong khi consumer vẫn chạy, và HR không có đường retry.</para>
///
/// <para>Bất biến chốt lại: KHÔNG tồn tại đường nào tạo được <c>cv_submission</c> cho một campaign
/// chưa có job_needs ⇒ mọi test dưới đây đếm số row trước/sau.</para>
/// </summary>
public class CampaignScreenRequiresJobNeedsCmp3B1Tests
{
    private static CampaignSvc NewService(CampaignDbContext db, params string[] parsedTexts)
    {
        var parser = new Mock<IParserService>();
        var seq = parser.SetupSequence(p => p.ParseAsync(It.IsAny<Stream>(), It.IsAny<CancellationToken>()));
        foreach (var t in parsedTexts.Length == 0 ? new[] { "cv text cand@x.com" } : parsedTexts)
            seq = seq.ReturnsAsync(new ParseResult { RawText = t });

        return new CampaignSvc(db, new Mock<IFileService>().Object,
            Mock.Of<ILogger<CampaignSvc>>(), parser.Object,
            Mock.Of<ICriteriaSuggester>(), Mock.Of<IInvitationEmailPublisher>());
    }

    private static IFormFile Pdf(string fileName = "cv.pdf", int bytes = 8)
    {
        var stream = new MemoryStream(Encoding.UTF8.GetBytes(new string('x', bytes)));
        return new FormFile(stream, 0, stream.Length, "files", fileName)
        {
            Headers = new HeaderDictionary(),
            ContentType = "application/pdf"
        };
    }

    private static IFormFileCollection Files(params IFormFile[] files)
    {
        var col = new FormFileCollection();
        col.AddRange(files);
        return col;
    }

    private static Guid SeedActive(CampaignTestDb tdb, Guid owner, List<JobNeed>? jobNeeds)
    {
        var camp = CampaignTestDb.NewCampaign(owner, CampaignStatus.Active);
        camp.JobNeeds = jobNeeds;
        tdb.Db.Campaigns.Add(camp);
        tdb.Db.SaveChanges();
        return camp.Id;
    }

    private static List<JobNeed> OneNeed() => new()
    {
        new() { NeedId = "n1", Category = JobNeedCategories.Technical, Text = "Thạo .NET", Source = JobNeedSources.HrEdited },
    };

    // (1) job_needs = null (hình dạng phổ biến: campaign Draft vừa publish mà AI hụt) ⇒ 409, và
    //     KHÔNG sinh row cv_submission nào. Đếm row trước/sau — vế quan trọng nhất.
    [Fact]
    public async Task JobNeeds_null_thi_409_va_khong_sinh_row_nao()
    {
        using var tdb = new CampaignTestDb();
        var owner = Guid.NewGuid();
        var campId = SeedActive(tdb, owner, jobNeeds: null);

        Assert.Equal(0, await tdb.NewContext().CvSubmissions.CountAsync(c => c.CampaignId == campId));

        var ex = await Assert.ThrowsAsync<InvalidOperationException>(() =>
            NewService(tdb.NewContext()).ScreenCandidatesAsync(
                owner, owner, campId, Files(Pdf("a.pdf"), Pdf("b.pdf")), default));

        Assert.Equal(0, await tdb.NewContext().CvSubmissions.CountAsync(c => c.CampaignId == campId));
        Assert.False(string.IsNullOrWhiteSpace(ex.Message));
    }

    // (2) job_needs = [] (mảng rỗng, KHÁC null) ở Active ⇒ 409. Đây là lỗ đang có: AI hụt lúc
    //     publish ⇒ campaign Active mà job_needs rỗng ⇒ cùng lời nói dối 6 giờ nếu không chặn.
    [Fact]
    public async Task JobNeeds_mang_rong_o_Active_cung_409_va_khong_sinh_row()
    {
        using var tdb = new CampaignTestDb();
        var owner = Guid.NewGuid();
        var campId = SeedActive(tdb, owner, jobNeeds: new List<JobNeed>());

        await Assert.ThrowsAsync<InvalidOperationException>(() =>
            NewService(tdb.NewContext()).ScreenCandidatesAsync(
                owner, owner, campId, Files(Pdf()), default));

        Assert.Equal(0, await tdb.NewContext().CvSubmissions.CountAsync(c => c.CampaignId == campId));
    }

    // (3) job_needs CÓ mục nhưng text toàn khoảng trắng ⇒ 409 — khớp vị ngữ của RequireJobNeeds
    //     (lọc theo !IsNullOrWhiteSpace(Text)). Không có mục nào "đo được" thì vẫn là rỗng.
    [Fact]
    public async Task JobNeeds_toan_text_trong_thi_409()
    {
        using var tdb = new CampaignTestDb();
        var owner = Guid.NewGuid();
        var campId = SeedActive(tdb, owner, jobNeeds: new List<JobNeed>
        {
            new() { NeedId = "n1", Category = JobNeedCategories.Technical, Text = "   ", Source = JobNeedSources.HrEdited },
        });

        await Assert.ThrowsAsync<InvalidOperationException>(() =>
            NewService(tdb.NewContext()).ScreenCandidatesAsync(
                owner, owner, campId, Files(Pdf()), default));

        Assert.Equal(0, await tdb.NewContext().CvSubmissions.CountAsync(c => c.CampaignId == campId));
    }

    // (4) job_needs CÓ ⇒ 202 như cũ: row Filtered được tạo, marker last_screening_published_at null
    //     (marker do PublishScreeningJobsAsync ở controller set, KHÔNG phải service này).
    [Fact]
    public async Task JobNeeds_co_muc_thi_van_sang_binh_thuong_row_Filtered_marker_null()
    {
        using var tdb = new CampaignTestDb();
        var owner = Guid.NewGuid();
        var campId = SeedActive(tdb, owner, jobNeeds: OneNeed());

        var res = await NewService(tdb.NewContext(), "kinh nghiem .NET, cand@x.com")
            .ScreenCandidatesAsync(owner, owner, campId, Files(Pdf()), default);

        Assert.Equal(1, res.Filtered);

        using var check = tdb.NewContext();
        var row = await check.CvSubmissions.SingleAsync(c => c.CampaignId == campId);
        Assert.Equal(CvSubmissionStatus.Filtered, row.Status);
        Assert.Null(row.LastScreeningPublishedAt);
    }

    // (5) Thông điệp 409 phải chỉ CÁCH SỬA (chốt nhu cầu công việc, và chốt ở đâu) — không chỉ mô
    //     tả hiện tượng.
    [Fact]
    public async Task Thong_diep_409_chi_ra_cach_chot_nhu_cau()
    {
        using var tdb = new CampaignTestDb();
        var owner = Guid.NewGuid();
        var campId = SeedActive(tdb, owner, jobNeeds: null);

        var ex = await Assert.ThrowsAsync<InvalidOperationException>(() =>
            NewService(tdb.NewContext()).ScreenCandidatesAsync(
                owner, owner, campId, Files(Pdf()), default));

        Assert.Contains("nhu cầu công việc", ex.Message);
        Assert.Contains("job-needs", ex.Message);
    }
}
