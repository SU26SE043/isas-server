using Isas.CampaignService.DTOs;
using Isas.CampaignService.Models;
using Isas.CampaignService.Services;
using Microsoft.AspNetCore.Http;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging;
using Moq;

// 'CampaignService' vừa là namespace vừa là tên class → alias cho rõ ràng.
using CampaignSvc = Isas.CampaignService.Services.CampaignService;

namespace Isas.CampaignService.Tests;

/// <summary>
/// C11 — JD & Criteria nhập TEXT trực tiếp (jdText/criteriaText), không bắt buộc PDF.
/// (a) tạo/sửa với text không file → *_text set, *_file_url null;
/// (b) text + file → text thắng, file bị bỏ;
/// (c) GET trả JD (text);
/// (d) publish vẫn sinh campaign_criteria từ text (AI đọc *_text bất kể nguồn).
/// </summary>
public class CampaignTextInputTests
{
    private static CampaignSvc NewService(
        CampaignDbContext db, ICriteriaSuggester? suggester = null, IParserService? parser = null) =>
        new(db, Mock.Of<IFileService>(), Mock.Of<ILogger<CampaignSvc>>(),
            parser ?? Mock.Of<IParserService>(),
            suggester ?? Mock.Of<ICriteriaSuggester>(),
            Mock.Of<IInvitationEmailPublisher>());

    // IFormFile PDF giả — dùng cho nhánh "text + file". ContentType/Length hợp lệ để CHỨNG minh
    // file bị BỎ (nếu không bỏ, parser sẽ ghi đè *_text → assertion thất bại).
    private static IFormFile MakePdf(string content = "%PDF-1.4 fake")
    {
        var bytes = System.Text.Encoding.UTF8.GetBytes(content);
        var mock = new Mock<IFormFile>();
        mock.Setup(f => f.ContentType).Returns("application/pdf");
        mock.Setup(f => f.Length).Returns(bytes.Length);
        mock.Setup(f => f.FileName).Returns("jd.pdf");
        mock.Setup(f => f.Name).Returns("file");
        mock.Setup(f => f.Headers).Returns(Mock.Of<IHeaderDictionary>());
        mock.Setup(f => f.OpenReadStream()).Returns(() => new MemoryStream(bytes));
        return mock.Object;
    }

    private static CreateCampaignRequest NewCreateReq(string? jdText = null, string? criteriaText = null) => new()
    {
        Title = "Tuyển BE",
        Domain = "BE",
        TimeLimitMinutes = 30,
        StartsAt = DateTime.UtcNow.AddDays(1),
        ExpiresAt = DateTime.UtcNow.AddDays(10),
        JdText = jdText,
        CriteriaText = criteriaText,
        // Không câu hỏi — cô lập hành vi text-input của create (question Id sinh bằng default DB
        // gen_random_uuid(), không có trên SQLite; publish mới đòi ≥1 câu — xem test (d)).
        Questions = new List<QuestionItem>()
    };

    // (a) Create với jdText/criteriaText (không file) → *_text set, *_file_url null.
    [Fact]
    public async Task Create_voi_text_truc_tiep_set_text_va_file_url_null()
    {
        using var tdb = new CampaignTestDb();
        var owner = Guid.NewGuid();
        var svc = NewService(tdb.NewContext());

        var res = await svc.CreateCampaignAsync(owner,
            NewCreateReq(jdText: "  JD nhập tay  ", criteriaText: "Tiêu chí nhập tay"), default);

        Assert.Equal("JD nhập tay", res.JDText);              // trim
        Assert.Equal("Tiêu chí nhập tay", res.CriteriaText);

        using var check = tdb.NewContext();
        var row = await check.Campaigns.FirstAsync(c => c.Id == res.Id);
        Assert.Equal("JD nhập tay", row.JDText);
        Assert.Equal("Tiêu chí nhập tay", row.CriteriaText);
        Assert.Null(row.JDFileUrl);
        Assert.Null(row.CriteriaFileUrl);
    }

    // (a) Update với jdText → set *_text, XOÁ *_file_url (text ưu tiên file trước đó).
    [Fact]
    public async Task Update_voi_jdText_set_text_va_xoa_file_url()
    {
        using var tdb = new CampaignTestDb();
        var owner = Guid.NewGuid();
        var camp = CampaignTestDb.NewCampaign(owner, CampaignStatus.Draft);
        camp.JDFileUrl = "campaigns/x/jd.pdf";   // trước đó nhập bằng PDF
        camp.JDText = "text từ PDF cũ";
        tdb.Db.Campaigns.Add(camp);
        await tdb.Db.SaveChangesAsync();

        var svc = NewService(tdb.NewContext());
        var res = await svc.UpdateCampaignAsync(owner, camp.Id,
            new UpdateCampaignRequest { Title = "Tuyển BE", JdText = "JD text mới" }, default);

        Assert.Equal("JD text mới", res.JDText);

        using var check = tdb.NewContext();
        var row = await check.Campaigns.FirstAsync(c => c.Id == camp.Id);
        Assert.Equal("JD text mới", row.JDText);
        Assert.Null(row.JDFileUrl);   // text thắng → file url bị xoá
    }

    // (b) Đã có text trực tiếp → upload file bị BỎ (text thắng, không ghi đè, không set file url).
    [Fact]
    public async Task Upload_file_khi_da_co_text_truc_tiep_thi_bo_file()
    {
        using var tdb = new CampaignTestDb();
        var owner = Guid.NewGuid();
        var camp = CampaignTestDb.NewCampaign(owner, CampaignStatus.Draft);
        camp.JDText = "JD nhập trực tiếp";   // text set, JDFileUrl null → "text trực tiếp"
        tdb.Db.Campaigns.Add(camp);
        await tdb.Db.SaveChangesAsync();

        // parser trả text KHÁC — nếu file KHÔNG bị bỏ, jd_text sẽ bị ghi đè thành "FROM-PDF".
        var parser = new Mock<IParserService>();
        parser.Setup(p => p.ParseAsync(It.IsAny<Stream>(), It.IsAny<CancellationToken>()))
              .ReturnsAsync(new ParseResult { RawText = "FROM-PDF" });

        var svc = NewService(tdb.NewContext(), parser: parser.Object);
        var res = await svc.UploadCampaignFilesAsync(owner, camp.Id,
            new UploadCampaignFilesRequest { JdFile = MakePdf() }, default);

        Assert.Equal("JD nhập trực tiếp", res.JDText);   // giữ text, không phải "FROM-PDF"

        using var check = tdb.NewContext();
        var row = await check.Campaigns.FirstAsync(c => c.Id == camp.Id);
        Assert.Equal("JD nhập trực tiếp", row.JDText);
        Assert.Null(row.JDFileUrl);                       // file bị bỏ → không set url
        parser.Verify(p => p.ParseAsync(It.IsAny<Stream>(), It.IsAny<CancellationToken>()), Times.Never);
    }

    // (c) GET trả JD (text) — kèm CriteriaText.
    [Fact]
    public async Task Get_tra_JD_text()
    {
        using var tdb = new CampaignTestDb();
        var owner = Guid.NewGuid();
        var camp = CampaignTestDb.NewCampaign(owner);
        camp.JDText = "JD hiển thị";
        camp.CriteriaText = "Tiêu chí hiển thị";
        tdb.Db.Campaigns.Add(camp);
        await tdb.Db.SaveChangesAsync();

        var svc = NewService(tdb.NewContext());
        var res = await svc.GetCampaignAsync(owner, camp.Id, default);

        Assert.Equal("JD hiển thị", res.JDText);
        Assert.Equal("Tiêu chí hiển thị", res.CriteriaText);
    }

    // (d) Publish sinh campaign_criteria từ text trực tiếp — AI đọc *_text bất kể nguồn.
    [Fact]
    public async Task Publish_sinh_criteria_tu_text_truc_tiep()
    {
        using var tdb = new CampaignTestDb();
        var owner = Guid.NewGuid();
        var camp = CampaignTestDb.NewCampaign(owner, CampaignStatus.Draft);
        camp.JDText = "JD text trực tiếp";
        camp.CriteriaText = "Tiêu chí text trực tiếp";
        tdb.Db.Campaigns.Add(camp);
        tdb.Db.CampaignQuestions.Add(new CampaignQuestion
        {
            Id = Guid.NewGuid(), CampaignId = camp.Id, EmployerId = owner,
            QuestionText = "Q1", Source = QuestionSource.CustomHr, IsRequired = true, CreatedAt = DateTime.UtcNow
        });
        await tdb.Db.SaveChangesAsync();

        var suggester = new Mock<ICriteriaSuggester>();
        suggester.Setup(s => s.SuggestAsync(It.IsAny<string>(), It.IsAny<string?>(),
                It.IsAny<string?>(), It.IsAny<int>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(new List<SuggestedCriterion> { new("C1", "d", 0.5m, 5), new("C2", "d", 0.5m, 5) });

        var svc = NewService(tdb.NewContext(), suggester.Object);
        var res = await svc.PublishCampaignAsync(owner, camp.Id, default);
        Assert.Equal("Active", res.Status);

        // AI nhận đúng *_text trực tiếp (bất kể nguồn text/PDF)
        suggester.Verify(s => s.SuggestAsync(It.IsAny<string>(),
            "JD text trực tiếp", "Tiêu chí text trực tiếp", It.IsAny<int>(), It.IsAny<CancellationToken>()), Times.Once);

        using var check = tdb.NewContext();
        var criteria = await check.CampaignCriteria.Where(c => c.CampaignId == camp.Id).ToListAsync();
        Assert.NotEmpty(criteria);
        Assert.Equal(1.0m, criteria.Sum(c => c.Weight));   // Σweight = 1
    }
}
