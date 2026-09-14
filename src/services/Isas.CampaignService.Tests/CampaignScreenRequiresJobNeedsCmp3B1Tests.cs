using Isas.CampaignService.DTOs;
using Isas.CampaignService.Models;
using Isas.CampaignService.Services;
using Microsoft.AspNetCore.Http;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Logging;
using Moq;
using System.Text;

using CampaignSvc = Isas.CampaignService.Services.CampaignService;

namespace Isas.CampaignService.Tests;

/// <summary>
/// SCR1-B1 — khi campaign CHƯA chốt <c>job_needs</c> lúc HR upload CV, <c>ScreenCandidatesAsync</c>
/// nay TỰ RÚT nhu cầu từ JD ngay tại đó (lazy-build, y hệt <see cref="CampaignSvc.PublishCampaignAsync"/>)
/// thay vì trả 409 ngay lập tức.
///
/// <para>ĐẢO TIỀN ĐỀ so với bản CMP3-B1 gốc: trước đây "job_needs rỗng" LUÔN 409 bất kể có JD hay
/// không — chủ sản phẩm chốt "HR chỉ dán JD → upload CV → có xếp hạng", không phải soạn job_needs
/// trước. Nay "rỗng" chỉ còn 409 khi KHÔNG rút được (thiếu JD, hoặc AI hỏng). Các test dưới đây
/// đổi kịch bản theo tiền đề mới; lý do ghi tại từng test.</para>
///
/// <para>Bất biến GIỮ NGUYÊN từ bản gốc: guard đứng TRƯỚC vòng lặp đọc file / archive S3 ⇒ mọi
/// nhánh throw ở đây (thiếu JD, AI hỏng) không sinh row <c>cv_submission</c> nào, không gọi
/// <c>IFileService</c>.</para>
/// </summary>
public class CampaignScreenRequiresJobNeedsCmp3B1Tests
{
    private static IConfiguration Config() =>
        new ConfigurationBuilder()
            .AddInMemoryCollection(new Dictionary<string, string?> { ["Internal:CallbackBase"] = "http://campaign:8080" })
            .Build();

    private static CampaignSvc NewService(
        CampaignDbContext db, Mock<IFileService>? file = null,
        IJobNeedsSuggester? suggester = null, params string[] parsedTexts)
    {
        var parser = new Mock<IParserService>();
        var seq = parser.SetupSequence(p => p.ParseAsync(It.IsAny<Stream>(), It.IsAny<CancellationToken>()));
        foreach (var t in parsedTexts.Length == 0 ? new[] { "cv text cand@x.com" } : parsedTexts)
            seq = seq.ReturnsAsync(new ParseResult { RawText = t });

        return new CampaignSvc(db, (file ?? new Mock<IFileService>()).Object,
            Mock.Of<ILogger<CampaignSvc>>(), parser.Object,
            Mock.Of<ICriteriaSuggester>(), Mock.Of<IInvitationEmailPublisher>(),
            jobNeedsSuggester: suggester);
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

    private static Guid SeedActive(
        CampaignTestDb tdb, Guid owner, List<JobNeed>? jobNeeds, string? jdText = null)
    {
        var camp = CampaignTestDb.NewCampaign(owner, CampaignStatus.Active);
        camp.JobNeeds = jobNeeds;
        camp.JDText = jdText;
        tdb.Db.Campaigns.Add(camp);
        tdb.Db.SaveChanges();
        return camp.Id;
    }

    private static List<JobNeed> OneNeed() => new()
    {
        new() { NeedId = "n1", Category = JobNeedCategories.Technical, Text = "Thạo .NET", Source = JobNeedSources.HrEdited },
    };

    private static IJobNeedsSuggester Suggester(params SuggestedJobNeed[] needs)
    {
        var m = new Mock<IJobNeedsSuggester>();
        m.Setup(s => s.SuggestAsync(It.IsAny<string>(), It.IsAny<string?>(), It.IsAny<string>(),
                                    It.IsAny<CancellationToken>()))
         .ReturnsAsync(needs.ToList());
        return m.Object;
    }

    // (1) needs rỗng + có JD + suggester trả 4 nhu cầu ⇒ THÀNH CÔNG (không 409): campaign.job_needs
    //     có 4 mục, tất cả Source=AiSuggested + IsMustHave=false (mẫu BuildJobNeedsAsync), row
    //     cv_submission được tạo (Filtered).
    [Fact]
    public async Task NeedsRong_CoJD_SuggesterTra4_ThanhCong_ChotJobNeeds_TaoRowFiltered()
    {
        using var tdb = new CampaignTestDb();
        var owner = Guid.NewGuid();
        var campId = SeedActive(tdb, owner, jobNeeds: null, jdText: "JD: cần Backend .NET, 2 năm kinh nghiệm");

        var suggester = Suggester(
            new SuggestedJobNeed(JobNeedCategories.Technical, "Thạo .NET"),
            new SuggestedJobNeed(JobNeedCategories.Technical, "Thạo SQL"),
            new SuggestedJobNeed(JobNeedCategories.Communication, "Giao tiếp tiếng Anh"),
            new SuggestedJobNeed(JobNeedCategories.Growth, "Học nhanh"));

        var res = await NewService(tdb.NewContext(), suggester: suggester, parsedTexts: "kinh nghiem .NET, cand@x.com")
            .ScreenCandidatesAsync(owner, owner, campId, Files(Pdf()), default);

        Assert.Equal(1, res.Filtered);

        using var check = tdb.NewContext();
        var saved = await check.Campaigns.FirstAsync(c => c.Id == campId);
        Assert.Equal(4, saved.JobNeeds!.Count);
        Assert.All(saved.JobNeeds, n => Assert.Equal(JobNeedSources.AiSuggested, n.Source));
        Assert.All(saved.JobNeeds, n => Assert.False(n.IsMustHave));

        var row = await check.CvSubmissions.SingleAsync(c => c.CampaignId == campId);
        Assert.Equal(CvSubmissionStatus.Filtered, row.Status);
    }

    // (1b) Bộ nhu cầu vừa lazy-build ĐỦ để bước sau (PublishScreeningJobsAsync/CvScreeningService,
    //     KHÔNG sửa) đẩy job sàng thật — RequireJobNeeds không còn ném. Đây là bằng chứng cho "có
    //     xếp hạng" trong tiêu đề task, không chỉ dừng ở việc campaign.job_needs được lưu.
    [Fact]
    public async Task NeedsRong_CoJD_SauKhiLazyBuild_PublishScreeningJobsDiDuoc()
    {
        using var tdb = new CampaignTestDb();
        var owner = Guid.NewGuid();
        var campId = SeedActive(tdb, owner, jobNeeds: null, jdText: "JD: cần Backend .NET");

        var suggester = Suggester(new SuggestedJobNeed(JobNeedCategories.Technical, "Thạo .NET"));

        await NewService(tdb.NewContext(), suggester: suggester, parsedTexts: "kinh nghiem .NET, cand@x.com")
            .ScreenCandidatesAsync(owner, owner, campId, Files(Pdf()), default);

        var screening = new CvScreeningService(tdb.NewContext(), Mock.Of<ICvScreeningPublisher>(), Config(),
            Mock.Of<ILogger<CvScreeningService>>());
        var published = await screening.PublishScreeningJobsAsync(owner, campId, default);

        Assert.Equal(1, published);   // KHÔNG ném — RequireJobNeeds thấy needs đã được lazy-build
    }

    // (2) needs rỗng + có JD + AI hỏng (suggester trả null) ⇒ 409 nêu rõ là LỖI AI, KHÔNG sinh row
    //     cv_submission nào, KHÔNG gọi IFileService (guard đứng TRƯỚC vòng lặp đọc file/archive S3).
    [Fact]
    public async Task NeedsRong_CoJD_SuggesterTraNull_409_MessageChuaAI_KhongTaoRow_KhongGoiFile()
    {
        using var tdb = new CampaignTestDb();
        var owner = Guid.NewGuid();
        var campId = SeedActive(tdb, owner, jobNeeds: null, jdText: "JD: cần Backend .NET");

        var dead = new Mock<IJobNeedsSuggester>();
        dead.Setup(s => s.SuggestAsync(It.IsAny<string>(), It.IsAny<string?>(), It.IsAny<string>(),
                                       It.IsAny<CancellationToken>()))
            .ReturnsAsync((List<SuggestedJobNeed>?)null);

        var fileMock = new Mock<IFileService>();

        var ex = await Assert.ThrowsAsync<InvalidOperationException>(() =>
            NewService(tdb.NewContext(), file: fileMock, suggester: dead.Object)
                .ScreenCandidatesAsync(owner, owner, campId, Files(Pdf("a.pdf"), Pdf("b.pdf")), default));

        Assert.Contains("AI", ex.Message);
        Assert.Equal(0, await tdb.NewContext().CvSubmissions.CountAsync(c => c.CampaignId == campId));
        fileMock.Verify(f => f.UploadAsync(It.IsAny<IFormFile>(), It.IsAny<string>(), It.IsAny<CancellationToken>()),
            Times.Never);
    }

    // (3) needs rỗng + JD TRỐNG ⇒ 409 nêu rõ thiếu JD, và KHÔNG gọi suggester (không có gì để gửi
    //     cho AI — không lãng phí một lượt gọi).
    [Fact]
    public async Task NeedsRong_JDTrong_409_MessageChuaJD_KhongGoiSuggester()
    {
        using var tdb = new CampaignTestDb();
        var owner = Guid.NewGuid();
        var campId = SeedActive(tdb, owner, jobNeeds: null, jdText: null);

        var suggester = new Mock<IJobNeedsSuggester>();

        var ex = await Assert.ThrowsAsync<InvalidOperationException>(() =>
            NewService(tdb.NewContext(), suggester: suggester.Object)
                .ScreenCandidatesAsync(owner, owner, campId, Files(Pdf()), default));

        Assert.Contains("JD", ex.Message);
        // Phân biệt với nhánh "AI lỗi" (message ~:2636 CŨNG chứa chữ "JD") — nếu guard thiếu-JD bị
        // bỏ qua và rơi xuống nhánh AI-lỗi (mutation M4), suggester vẫn không được gọi (short-circuit
        // BÊN TRONG BuildJobNeedsAsync khi JDText rỗng) nên riêng Times.Never không bắt được; phải
        // khoá bằng chính NỘI DUNG thông điệp.
        Assert.DoesNotContain("AI", ex.Message);
        Assert.Equal(0, await tdb.NewContext().CvSubmissions.CountAsync(c => c.CampaignId == campId));
        suggester.Verify(s => s.SuggestAsync(It.IsAny<string>(), It.IsAny<string?>(), It.IsAny<string>(),
            It.IsAny<CancellationToken>()), Times.Never);
    }

    // (3c) Review khi gộp SCR1-B1: kiểm files/cap 400 phải chạy TRƯỚC lazy-build. Request KHÔNG có
    //     file nào ⇒ 400 ngay, suggester KHÔNG được gọi — nếu khối lazy đứng trước kiểm files thì
    //     mỗi request rỗng đốt 1 lượt Gemini rồi vứt (throw ArgumentException trước SaveChanges ⇒
    //     needs vừa rút cũng mất, lần sau lại gọi AI tiếp).
    [Fact]
    public async Task NeedsRong_CoJD_KhongCoFile_400_KhongGoiSuggester()
    {
        using var tdb = new CampaignTestDb();
        var owner = Guid.NewGuid();
        var campId = SeedActive(tdb, owner, jobNeeds: null, jdText: "Cần .NET + Postgres");

        var suggester = new Mock<IJobNeedsSuggester>();

        await Assert.ThrowsAsync<ArgumentException>(() =>
            NewService(tdb.NewContext(), suggester: suggester.Object)
                .ScreenCandidatesAsync(owner, owner, campId, Files(), default));

        suggester.Verify(s => s.SuggestAsync(It.IsAny<string>(), It.IsAny<string?>(), It.IsAny<string>(),
            It.IsAny<CancellationToken>()), Times.Never);
        var saved = await tdb.NewContext().Campaigns.SingleAsync(c => c.Id == campId);
        Assert.True(saved.JobNeeds is null || saved.JobNeeds.Count == 0);
    }

    // (3b) JD toàn khoảng trắng (KHÁC null, giống lỗi thường gặp khi HR paste rồi xoá) ⇒ vẫn coi
    //     là "chưa có JD" (khớp guard dùng IsNullOrWhiteSpace) — 409, không gọi suggester.
    [Fact]
    public async Task NeedsRong_JDToanKhoangTrang_CungBi409_KhongGoiSuggester()
    {
        using var tdb = new CampaignTestDb();
        var owner = Guid.NewGuid();
        var campId = SeedActive(tdb, owner, jobNeeds: null, jdText: "   ");

        var suggester = new Mock<IJobNeedsSuggester>();

        var ex = await Assert.ThrowsAsync<InvalidOperationException>(() =>
            NewService(tdb.NewContext(), suggester: suggester.Object)
                .ScreenCandidatesAsync(owner, owner, campId, Files(Pdf()), default));

        Assert.Contains("JD", ex.Message);
        Assert.DoesNotContain("AI", ex.Message);
        suggester.Verify(s => s.SuggestAsync(It.IsAny<string>(), It.IsAny<string?>(), It.IsAny<string>(),
            It.IsAny<CancellationToken>()), Times.Never);
    }

    // (4) needs rỗng + có JD + MỌI file trong batch đều bị BỎ QUA trước khi tạo row (created.Count
    //     == 0) ⇒ nhu cầu vừa lazy-build VẪN phải được lưu — không thì lần sàng kế tiếp lại thấy
    //     "chưa có needs" và tốn thêm 1 lượt AI vô ích.
    //
    //     ⚠ Đường duy nhất cho created.Count == 0 với `files` không rỗng là DEDUP EMAIL: một PDF hỏng
    //     (sai định dạng/parse fail) vẫn LUÔN tạo được 1 row Rejected (xem `NewRejectedCandidate`),
    //     nên "PDF hỏng" không thể tự nó cho ra 0 created. Seed sẵn 1 cv_submission cùng email trong
    //     campaign để cả batch bị dedup-skip là cách DUY NHẤT tái hiện đúng ca created.Count == 0.
    [Fact]
    public async Task NeedsRong_CoJD_TatCaFileTrungEmailCoSan_0Created_NeedsVanDuocLuu()
    {
        using var tdb = new CampaignTestDb();
        var owner = Guid.NewGuid();
        var campId = SeedActive(tdb, owner, jobNeeds: null, jdText: "JD: cần Backend .NET");

        tdb.Db.CvSubmissions.Add(new CvSubmission
        {
            Id = Guid.NewGuid(),
            CampaignId = campId,
            Email = "cand@x.com",
            CvParsedText = "CV cũ",
            ParseStatus = CvParseStatus.Done,
            Status = CvSubmissionStatus.Filtered,
            CreatedAt = DateTime.UtcNow,
            UpdatedAt = DateTime.UtcNow,
        });
        tdb.Db.SaveChanges();

        var suggester = Suggester(new SuggestedJobNeed(JobNeedCategories.Technical, "Thạo .NET"));

        var res = await NewService(tdb.NewContext(), suggester: suggester,
                parsedTexts: "kinh nghiem .NET, cand@x.com")
            .ScreenCandidatesAsync(owner, owner, campId, Files(Pdf()), default);

        // 0 row MỚI được tạo (file duy nhất trùng email với row đã seed sẵn ⇒ dedup-skip).
        Assert.Equal(0, res.Filtered);
        Assert.Equal(0, res.Rejected);
        Assert.Equal(1, res.Skipped);
        Assert.Equal(1, await tdb.NewContext().CvSubmissions.CountAsync(c => c.CampaignId == campId));

        using var check = tdb.NewContext();
        var saved = await check.Campaigns.FirstAsync(c => c.Id == campId);
        Assert.Single(saved.JobNeeds!);
        Assert.Equal("Thạo .NET", saved.JobNeeds![0].Text);
    }

    // (4b) Lazy-build KHÔNG chỉ hiển thị đúng trong cùng lượt gọi — nó phải PERSIST xuống DB để lượt
    //     upload KẾ TIẾP (context/service instance MỚI, đúng hình dạng 2 request HTTP khác nhau)
    //     đọc lại thấy campaign ĐÃ có needs và KHÔNG gọi suggester lần nữa (khỏi tốn thêm 1 lượt AI).
    [Fact]
    public async Task NeedsRong_CoJD_SauKhiLazyBuild_LanUploadSauKhongGoiLaiSuggester()
    {
        using var tdb = new CampaignTestDb();
        var owner = Guid.NewGuid();
        var campId = SeedActive(tdb, owner, jobNeeds: null, jdText: "JD: cần Backend .NET");

        var suggester = Suggester(new SuggestedJobNeed(JobNeedCategories.Technical, "Thạo .NET"));

        // Lượt 1 — campaign chưa có needs ⇒ lazy-build + lưu.
        await NewService(tdb.NewContext(), suggester: suggester, parsedTexts: "cv, cand1@x.com")
            .ScreenCandidatesAsync(owner, owner, campId, Files(Pdf("a.pdf")), default);

        // Lượt 2 — service/context MỚI (mô phỏng request HTTP khác), campaign giờ đã có needs
        // (đọc lại từ DB, không phải state trong bộ nhớ của lượt 1).
        await NewService(tdb.NewContext(), suggester: suggester, parsedTexts: "cv, cand2@x.com")
            .ScreenCandidatesAsync(owner, owner, campId, Files(Pdf("b.pdf")), default);

        Mock.Get(suggester).Verify(s => s.SuggestAsync(It.IsAny<string>(), It.IsAny<string?>(), It.IsAny<string>(),
            It.IsAny<CancellationToken>()), Times.Once);   // TỔNG cả 2 lượt — không phải lượt nào cũng gọi

        Assert.Equal(2, await tdb.NewContext().CvSubmissions.CountAsync(c => c.CampaignId == campId));
    }

    // (5) đã có needs (HrEdited) ⇒ suggester KHÔNG được gọi (Times.Never), bộ nhu cầu giữ nguyên
    //     (KHÔNG lazy-build đè lên quyết định của HR — mẫu PublishCampaignAsync "AI không đè lên
    //     bộ HR đã sửa").
    [Fact]
    public async Task DaCoNeeds_HrEdited_SuggesterKhongDuocGoi_NeedsGiuNguyen()
    {
        using var tdb = new CampaignTestDb();
        var owner = Guid.NewGuid();
        var campId = SeedActive(tdb, owner, jobNeeds: OneNeed(), jdText: "JD: cần Backend .NET");

        var suggester = new Mock<IJobNeedsSuggester>();

        var res = await NewService(tdb.NewContext(), suggester: suggester.Object,
                parsedTexts: "kinh nghiem .NET, cand@x.com")
            .ScreenCandidatesAsync(owner, owner, campId, Files(Pdf()), default);

        Assert.Equal(1, res.Filtered);
        suggester.Verify(s => s.SuggestAsync(It.IsAny<string>(), It.IsAny<string?>(), It.IsAny<string>(),
            It.IsAny<CancellationToken>()), Times.Never);

        using var check = tdb.NewContext();
        var saved = await check.Campaigns.FirstAsync(c => c.Id == campId);
        Assert.Single(saved.JobNeeds!);
        Assert.Equal("Thạo .NET", saved.JobNeeds![0].Text);
        Assert.Equal(JobNeedSources.HrEdited, saved.JobNeeds![0].Source);
    }
}
