using System.Text;
using Isas.CampaignService.DTOs;
using Isas.CampaignService.Models;
using Isas.CampaignService.Services;
using Microsoft.AspNetCore.Http;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging;
using Moq;

using CampaignSvc = Isas.CampaignService.Services.CampaignService;

namespace Isas.CampaignService.Tests;

/// <summary>
/// EVA1-B5 / HĐ-2 — mở đường API để HR khai 3 luật lọc CỨNG sàng CV (D19: lá chắn chi phí Gemini
/// số 1 — hard-filter TRƯỚC AI). Ba cột đã tồn tại và <c>RunHardFilter</c> chạy thật, nhưng KHÔNG
/// có DTO nào để ghi ⇒ luôn trả "qua" ⇒ hybrid 2 tầng còn 1 tầng ⇒ 100% CV đi thẳng vào Gemini.
///
/// <para>Lỗ sống lâu vì test cũ gán THẲNG <c>camp.RequiredSkills = ...</c> lên entity, đi vòng qua
/// tầng API. Bộ này ĐI QUA <c>CreateCampaignAsync</c>/<c>UpdateCampaignAsync</c>.</para>
/// </summary>
public class CampaignHardFilterWiringEva1B5Tests
{
    private static CampaignSvc NewService(CampaignDbContext db, params string[] parsedTexts)
    {
        var parser = new Mock<IParserService>();
        var seq = parser.SetupSequence(p => p.ParseAsync(It.IsAny<Stream>(), It.IsAny<CancellationToken>()));
        foreach (var t in parsedTexts)
            seq = seq.ReturnsAsync(new ParseResult { RawText = t });

        return new CampaignSvc(db, Mock.Of<IFileService>(), Mock.Of<ILogger<CampaignSvc>>(),
            parser.Object, Mock.Of<ICriteriaSuggester>(), Mock.Of<IInvitationEmailPublisher>());
    }

    private static CreateCampaignRequest NewCreateReq(
        List<string>? requiredSkills = null, List<string>? keywordsAny = null, int? minYears = null) => new()
    {
        Title = "Tuyển BE",
        Domain = "BE",
        TimeLimitMinutes = 30,
        StartsAt = DateTime.UtcNow.AddDays(1),
        ExpiresAt = DateTime.UtcNow.AddDays(10),
        RequiredSkills = requiredSkills,
        KeywordsAny = keywordsAny,
        MinYearsExperience = minYears,
        Questions = new List<QuestionItem>()
    };

    private static async Task SetActiveAsync(CampaignTestDb tdb, Guid campaignId)
    {
        using var c = tdb.NewContext();
        var camp = await c.Campaigns.FirstAsync(x => x.Id == campaignId);
        camp.Status = CampaignStatus.Active;
        await c.SaveChangesAsync();
    }

    private static async Task SeedCandidateAsync(CampaignTestDb tdb, Guid campaignId, string email)
    {
        using var c = tdb.NewContext();
        c.CvSubmissions.Add(new CvSubmission
        {
            Id = Guid.NewGuid(), CampaignId = campaignId, Email = email, CvParsedText = "CV",
            ParseStatus = CvParseStatus.Done, Status = CvSubmissionStatus.Filtered,
            CreatedAt = DateTime.UtcNow, UpdatedAt = DateTime.UtcNow
        });
        await c.SaveChangesAsync();
    }

    private static IFormFileCollection OnePdf()
    {
        var stream = new MemoryStream(Encoding.UTF8.GetBytes("xxxxxxxx"));
        var col = new FormFileCollection
        {
            new FormFile(stream, 0, stream.Length, "files", "cv.pdf")
            {
                Headers = new HeaderDictionary(), ContentType = "application/pdf"
            }
        };
        return col;
    }

    // (a) POST rồi GET trả lại đủ 3 trường; mục chỉ khoảng trắng bị loại lặng.
    [Fact]
    public async Task Post_roi_Get_tra_lai_du_3_truong()
    {
        using var tdb = new CampaignTestDb();
        var owner = Guid.NewGuid();

        var created = await NewService(tdb.NewContext()).CreateCampaignAsync(owner, owner,
            NewCreateReq(requiredSkills: new() { "Kubernetes", "   " },
                        keywordsAny: new() { "Docker" }, minYears: 3), default);

        Assert.Equal(new[] { "Kubernetes" }, created.RequiredSkills);   // "   " loại lặng
        Assert.Equal(new[] { "Docker" }, created.KeywordsAny);
        Assert.Equal(3, created.MinYearsExperience);

        var got = await NewService(tdb.NewContext()).GetCampaignAsync(owner, created.Id, default);
        Assert.Equal(new[] { "Kubernetes" }, got.RequiredSkills);
        Assert.Equal(new[] { "Docker" }, got.KeywordsAny);
        Assert.Equal(3, got.MinYearsExperience);
    }

    // (b) PUT null = KHÔNG ĐỔI; PUT [] (+ minYears 0) = XOÁ luật.
    [Fact]
    public async Task Put_null_khong_doi_Put_rong_thi_xoa()
    {
        using var tdb = new CampaignTestDb();
        var owner = Guid.NewGuid();
        var created = await NewService(tdb.NewContext()).CreateCampaignAsync(owner, owner,
            NewCreateReq(requiredSkills: new() { "K8s" }, keywordsAny: new() { "Docker" }, minYears: 5), default);

        // null/vắng → giữ nguyên cấu hình lọc
        var afterNull = await NewService(tdb.NewContext()).UpdateCampaignAsync(owner, owner, created.Id,
            new UpdateCampaignRequest { Title = "Đổi tên thôi" }, default);
        Assert.Equal(new[] { "K8s" }, afterNull.RequiredSkills);
        Assert.Equal(new[] { "Docker" }, afterNull.KeywordsAny);
        Assert.Equal(5, afterNull.MinYearsExperience);

        // [] + 0 → xoá luật (RunHardFilter chỉ áp khi Count > 0 / min > 0)
        var afterClear = await NewService(tdb.NewContext()).UpdateCampaignAsync(owner, owner, created.Id,
            new UpdateCampaignRequest
            {
                RequiredSkills = new List<string>(),
                KeywordsAny = new List<string>(),
                MinYearsExperience = 0
            }, default);
        Assert.Null(afterClear.RequiredSkills);
        Assert.Null(afterClear.KeywordsAny);
        Assert.Equal(0, afterClear.MinYearsExperience);

        var got = await NewService(tdb.NewContext()).GetCampaignAsync(owner, created.Id, default);
        Assert.Null(got.RequiredSkills);
        Assert.Null(got.KeywordsAny);
        Assert.Equal(0, got.MinYearsExperience);
    }

    // (c) minYearsExperience ngoài [0, 60] → ArgumentException (→400).
    [Fact]
    public async Task MinYears_ngoai_khoang_nem_ArgumentException()
    {
        using var tdb = new CampaignTestDb();
        var owner = Guid.NewGuid();

        await Assert.ThrowsAsync<ArgumentException>(() =>
            NewService(tdb.NewContext()).CreateCampaignAsync(owner, owner, NewCreateReq(minYears: -1), default));

        await Assert.ThrowsAsync<ArgumentException>(() =>
            NewService(tdb.NewContext()).CreateCampaignAsync(owner, owner, NewCreateReq(minYears: 61), default));
    }

    // (d) Cửa trạng thái: Active + ĐÃ CÓ ứng viên → 409 (InvalidOperationException); Active chưa có
    //     ứng viên thì vẫn sửa được (đối chứng để 409 là về ỨNG VIÊN, không phải về Active).
    [Fact]
    public async Task Update_Active_da_co_ung_vien_nem_InvalidOperationException()
    {
        using var tdb = new CampaignTestDb();
        var owner = Guid.NewGuid();
        var created = await NewService(tdb.NewContext()).CreateCampaignAsync(owner, owner, NewCreateReq(), default);
        await SetActiveAsync(tdb, created.Id);

        // Active + chưa có ứng viên → sửa được
        var ok = await NewService(tdb.NewContext()).UpdateCampaignAsync(owner, owner, created.Id,
            new UpdateCampaignRequest { RequiredSkills = new() { "K8s" } }, default);
        Assert.Equal(new[] { "K8s" }, ok.RequiredSkills);

        // Active + đã có ứng viên → 409
        await SeedCandidateAsync(tdb, created.Id, "cand@x.com");
        await Assert.ThrowsAsync<InvalidOperationException>(() =>
            NewService(tdb.NewContext()).UpdateCampaignAsync(owner, owner, created.Id,
                new UpdateCampaignRequest { KeywordsAny = new() { "Docker" } }, default));
    }

    // (e) end-to-end: khai requiredSkills=["Kubernetes"] QUA API → upload CV không có từ đó →
    //     ứng viên Rejected, rejectReason chứa "Kubernetes".
    [Fact]
    public async Task EndToEnd_requiredSkill_khai_qua_API_thi_loc_cung_chay()
    {
        using var tdb = new CampaignTestDb();
        var owner = Guid.NewGuid();

        var created = await NewService(tdb.NewContext()).CreateCampaignAsync(owner, owner,
            NewCreateReq(requiredSkills: new() { "Kubernetes" }), default);
        await SetActiveAsync(tdb, created.Id);   // ScreenCandidatesAsync đòi Active

        var res = await NewService(tdb.NewContext(), "Java Python Docker — cand@x.com")
            .ScreenCandidatesAsync(owner, owner, created.Id, OnePdf(), default);

        Assert.Equal(1, res.Rejected);
        Assert.Equal(0, res.Filtered);

        using var check = tdb.NewContext();
        var row = await check.CvSubmissions.SingleAsync(r => r.CampaignId == created.Id);
        Assert.Equal(CvSubmissionStatus.Rejected, row.Status);
        Assert.Contains("Kubernetes", row.RejectReason);
    }
}
