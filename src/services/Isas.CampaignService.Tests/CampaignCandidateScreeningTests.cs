using Isas.CampaignService.DTOs;
using Isas.CampaignService.Models;
using Isas.CampaignService.Services;
using Microsoft.AspNetCore.Http;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging;
using Moq;
using System.Text;

// 'CampaignService' vừa là namespace vừa là tên class → alias.
using CampaignSvc = Isas.CampaignService.Services.CampaignService;

namespace Isas.CampaignService.Tests;

/// <summary>
/// C13 — Sàng CV hàng loạt: parse + archive PDF (S3 KEY) + hard-filter (rule cứng, 0 credit).
/// (a) bulk POST → rows campaign_candidates (+cv_file_url KEY);
/// (b) parse fail → Rejected(reason);
/// (c) thiếu skill bắt buộc → Rejected; qua → Filtered;
/// (d) trùng email → skip (không tạo row);
/// (e) vượt cap max_candidates → ArgumentException (→400);
/// (f) campaign chưa Active → InvalidOperationException (→409);
/// (g) GET cv → stream / null → FileNotFoundException(404) / ngoài org → KeyNotFoundException(404).
/// Parser + FileService mock (cô lập hard-filter/archive; không cần PDF thật). Controller map
/// ArgumentException→400, InvalidOperationException→409, KeyNotFound/FileNotFound→404.
/// </summary>
public class CampaignCandidateScreeningTests
{
    // Parser mock trả lần lượt text cho từng file (theo thứ tự upload).
    private static CampaignSvc NewService(
        CampaignDbContext db, IEnumerable<string> parsedTexts, Mock<IFileService>? file = null)
    {
        var parser = new Mock<IParserService>();
        var seq = parser.SetupSequence(p => p.ParseAsync(It.IsAny<Stream>(), It.IsAny<CancellationToken>()));
        foreach (var t in parsedTexts)
            seq = seq.ReturnsAsync(new ParseResult { RawText = t });

        return new CampaignSvc(db, (file ?? new Mock<IFileService>()).Object,
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

    private static Campaign SeedCampaign(
        CampaignTestDb tdb, Guid owner,
        CampaignStatus status = CampaignStatus.Active,
        int? maxCandidates = null,
        List<string>? requiredSkills = null,
        List<string>? keywordsAny = null,
        int? minYears = null)
    {
        var camp = CampaignTestDb.NewCampaign(owner, status);
        camp.MaxCandidates = maxCandidates;
        camp.RequiredSkills = requiredSkills;
        camp.KeywordsAny = keywordsAny;
        camp.MinYearsExperience = minYears;
        tdb.Db.Campaigns.Add(camp);
        tdb.Db.SaveChanges();
        return camp;
    }

    // (a) 2 PDF hợp lệ, không rule → cả 2 Filtered; mỗi row có cv_file_url = KEY (không full URL).
    [Fact]
    public async Task Bulk_upload_tao_rows_campaign_candidates_voi_cv_file_url_KEY()
    {
        using var tdb = new CampaignTestDb();
        var owner = Guid.NewGuid();
        var camp = SeedCampaign(tdb, owner);

        var svc = NewService(tdb.NewContext(), new[] { "CV một a@x.com", "CV hai b@x.com" });
        var res = await svc.ScreenCandidatesAsync(owner, owner, camp.Id, Files(Pdf("a.pdf"), Pdf("b.pdf")), default);

        Assert.Equal(2, res.Received);
        Assert.Equal(2, res.Filtered);
        Assert.Equal(0, res.Rejected);
        Assert.Equal(0, res.Skipped);

        using var check = tdb.NewContext();
        var rows = await check.CampaignCandidates.Where(c => c.CampaignId == camp.Id).ToListAsync();
        Assert.Equal(2, rows.Count);
        Assert.All(rows, r =>
        {
            Assert.Equal(CandidateStatus.Filtered, r.Status);
            Assert.Equal(CvParseStatus.Done, r.ParseStatus);
            Assert.NotNull(r.CvFileUrl);
            Assert.StartsWith($"campaigns/{camp.Id}/candidates/", r.CvFileUrl);   // KEY, không full URL (GEN-5)
            Assert.EndsWith(".pdf", r.CvFileUrl);
            Assert.DoesNotContain("http", r.CvFileUrl!);
        });
        // audit ghi 1 dòng ScreenCandidates
        Assert.True(await check.AuditLogs.AnyAsync(a => a.Action == AuditAction.ScreenCandidates && a.EntityId == camp.Id));
    }

    // (b) parse fail (text rỗng) → Rejected + reject_reason; parse_status=Failed; VẪN archive cv_file_url.
    [Fact]
    public async Task Parse_fail_thi_Rejected_co_reason()
    {
        using var tdb = new CampaignTestDb();
        var owner = Guid.NewGuid();
        var camp = SeedCampaign(tdb, owner);

        var svc = NewService(tdb.NewContext(), new[] { "" });   // parser trả rỗng = parse fail
        var res = await svc.ScreenCandidatesAsync(owner, owner, camp.Id, Files(Pdf()), default);

        Assert.Equal(1, res.Rejected);
        Assert.Equal(0, res.Filtered);

        using var check = tdb.NewContext();
        var row = await check.CampaignCandidates.SingleAsync(c => c.CampaignId == camp.Id);
        Assert.Equal(CandidateStatus.Rejected, row.Status);
        Assert.Equal(CvParseStatus.Failed, row.ParseStatus);
        Assert.False(string.IsNullOrWhiteSpace(row.RejectReason));
        Assert.NotNull(row.CvFileUrl);   // archive để HR xem file gốc dù loại
    }

    // (c) required_skills: thiếu → Rejected(reason chứa skill); có đủ → Filtered.
    [Fact]
    public async Task Hard_filter_thieu_required_skill_Rejected_co_thi_Filtered()
    {
        using var tdb = new CampaignTestDb();
        var owner = Guid.NewGuid();
        var camp = SeedCampaign(tdb, owner, requiredSkills: new() { "SQL" });

        // file1 không có SQL → Rejected; file2 có SQL → Filtered.
        var svc = NewService(tdb.NewContext(), new[] { "Java Python a@x.com", "SQL Java b@x.com" });
        var res = await svc.ScreenCandidatesAsync(owner, owner, camp.Id, Files(Pdf("1.pdf"), Pdf("2.pdf")), default);

        Assert.Equal(1, res.Rejected);
        Assert.Equal(1, res.Filtered);

        using var check = tdb.NewContext();
        var rows = await check.CampaignCandidates.Where(c => c.CampaignId == camp.Id).ToListAsync();
        var rejected = rows.Single(r => r.Status == CandidateStatus.Rejected);
        var filtered = rows.Single(r => r.Status == CandidateStatus.Filtered);
        Assert.Contains("SQL", rejected.RejectReason);
        Assert.Null(filtered.RejectReason);
    }

    // (c-bis) keywords_any: không có từ khóa nào → Rejected; có ≥1 → Filtered.
    [Fact]
    public async Task Hard_filter_keywords_any_khong_co_Rejected()
    {
        using var tdb = new CampaignTestDb();
        var owner = Guid.NewGuid();
        var camp = SeedCampaign(tdb, owner, keywordsAny: new() { "React", "Angular" });

        var svc = NewService(tdb.NewContext(), new[] { "Backend Java a@x.com", "React dev b@x.com" });
        var res = await svc.ScreenCandidatesAsync(owner, owner, camp.Id, Files(Pdf("1.pdf"), Pdf("2.pdf")), default);

        Assert.Equal(1, res.Rejected);
        Assert.Equal(1, res.Filtered);
    }

    // (d) trùng email → BỎ QUA (không tạo row); chỉ 1 row persist, Skipped=1.
    [Fact]
    public async Task Trung_email_thi_skip_khong_tao_row()
    {
        using var tdb = new CampaignTestDb();
        var owner = Guid.NewGuid();
        var camp = SeedCampaign(tdb, owner);

        // cả 2 CV cùng email → file2 skip.
        var svc = NewService(tdb.NewContext(), new[] { "CV một dup@x.com", "CV hai dup@x.com" });
        var res = await svc.ScreenCandidatesAsync(owner, owner, camp.Id, Files(Pdf("1.pdf"), Pdf("2.pdf")), default);

        Assert.Equal(2, res.Received);
        Assert.Equal(1, res.Filtered);
        Assert.Equal(1, res.Skipped);

        using var check = tdb.NewContext();
        Assert.Equal(1, await check.CampaignCandidates.CountAsync(c => c.CampaignId == camp.Id));
    }

    // (d-bis) dedup xuyên request: email đã tồn tại trong campaign → upload lại cùng email = skip.
    [Fact]
    public async Task Dedup_xuyen_request_email_da_ton_tai_skip()
    {
        using var tdb = new CampaignTestDb();
        var owner = Guid.NewGuid();
        var camp = SeedCampaign(tdb, owner);
        tdb.Db.CampaignCandidates.Add(new CampaignCandidate
        {
            Id = Guid.NewGuid(), CampaignId = camp.Id, Email = "old@x.com",
            ParseStatus = CvParseStatus.Done, Status = CandidateStatus.Filtered,
            CreatedAt = DateTime.UtcNow, UpdatedAt = DateTime.UtcNow
        });
        await tdb.Db.SaveChangesAsync();

        var svc = NewService(tdb.NewContext(), new[] { "CV trùng old@x.com" });
        var res = await svc.ScreenCandidatesAsync(owner, owner, camp.Id, Files(Pdf()), default);

        Assert.Equal(1, res.Skipped);
        Assert.Equal(0, res.Filtered);
        using var check = tdb.NewContext();
        Assert.Equal(1, await check.CampaignCandidates.CountAsync(c => c.CampaignId == camp.Id));   // vẫn 1 (không thêm)
    }

    // (e) vượt cap max_candidates → ArgumentException (→400), không tạo row nào.
    [Fact]
    public async Task Vuot_cap_max_candidates_nem_ArgumentException()
    {
        using var tdb = new CampaignTestDb();
        var owner = Guid.NewGuid();
        var camp = SeedCampaign(tdb, owner, maxCandidates: 1);

        var svc = NewService(tdb.NewContext(), new[] { "a@x.com", "b@x.com" });
        await Assert.ThrowsAsync<ArgumentException>(() =>
            svc.ScreenCandidatesAsync(owner, owner, camp.Id, Files(Pdf("1.pdf"), Pdf("2.pdf")), default));

        using var check = tdb.NewContext();
        Assert.Equal(0, await check.CampaignCandidates.CountAsync(c => c.CampaignId == camp.Id));
    }

    // (f) campaign chưa Active (Draft) → InvalidOperationException (→409).
    [Fact]
    public async Task Campaign_chua_Active_nem_InvalidOperationException()
    {
        using var tdb = new CampaignTestDb();
        var owner = Guid.NewGuid();
        var camp = SeedCampaign(tdb, owner, status: CampaignStatus.Draft);

        var svc = NewService(tdb.NewContext(), new[] { "a@x.com" });
        await Assert.ThrowsAsync<InvalidOperationException>(() =>
            svc.ScreenCandidatesAsync(owner, owner, camp.Id, Files(Pdf()), default));
    }

    // (f-bis) campaign không tồn tại / ngoài org → KeyNotFoundException (→404).
    [Fact]
    public async Task Campaign_ngoai_org_nem_KeyNotFoundException()
    {
        using var tdb = new CampaignTestDb();
        var owner = Guid.NewGuid();
        var camp = SeedCampaign(tdb, owner);

        var svc = NewService(tdb.NewContext(), new[] { "a@x.com" });
        await Assert.ThrowsAsync<KeyNotFoundException>(() =>
            svc.ScreenCandidatesAsync(Guid.NewGuid() /* người khác */, Guid.NewGuid(), camp.Id, Files(Pdf()), default));
    }

    // (g) GET cv: có cv_file_url → trả stream nội dung.
    [Fact]
    public async Task Download_cv_tra_stream_khi_co_file()
    {
        using var tdb = new CampaignTestDb();
        var owner = Guid.NewGuid();
        var camp = SeedCampaign(tdb, owner);
        var cid = Guid.NewGuid();
        var key = $"campaigns/{camp.Id}/candidates/{cid}.pdf";
        tdb.Db.CampaignCandidates.Add(new CampaignCandidate
        {
            Id = cid, CampaignId = camp.Id, CvFileUrl = key,
            ParseStatus = CvParseStatus.Done, Status = CandidateStatus.Filtered,
            CreatedAt = DateTime.UtcNow, UpdatedAt = DateTime.UtcNow
        });
        await tdb.Db.SaveChangesAsync();

        var file = new Mock<IFileService>();
        file.Setup(f => f.DownloadAsync(key, It.IsAny<CancellationToken>()))
            .ReturnsAsync(() => new MemoryStream(Encoding.UTF8.GetBytes("PDFBYTES")));

        var svc = new CampaignSvc(tdb.NewContext(), file.Object, Mock.Of<ILogger<CampaignSvc>>(),
            Mock.Of<IParserService>(), Mock.Of<ICriteriaSuggester>(), Mock.Of<IInvitationEmailPublisher>());

        await using var stream = await svc.DownloadCandidateCvAsync(owner, camp.Id, cid, default);
        using var reader = new StreamReader(stream);
        Assert.Equal("PDFBYTES", await reader.ReadToEndAsync());
    }

    // (g-bis) GET cv: cv_file_url null → FileNotFoundException (→404).
    [Fact]
    public async Task Download_cv_null_key_nem_FileNotFoundException()
    {
        using var tdb = new CampaignTestDb();
        var owner = Guid.NewGuid();
        var camp = SeedCampaign(tdb, owner);
        var cid = Guid.NewGuid();
        tdb.Db.CampaignCandidates.Add(new CampaignCandidate
        {
            Id = cid, CampaignId = camp.Id, CvFileUrl = null,
            ParseStatus = CvParseStatus.Failed, Status = CandidateStatus.Rejected,
            CreatedAt = DateTime.UtcNow, UpdatedAt = DateTime.UtcNow
        });
        await tdb.Db.SaveChangesAsync();

        var svc = new CampaignSvc(tdb.NewContext(), Mock.Of<IFileService>(), Mock.Of<ILogger<CampaignSvc>>(),
            Mock.Of<IParserService>(), Mock.Of<ICriteriaSuggester>(), Mock.Of<IInvitationEmailPublisher>());

        await Assert.ThrowsAsync<FileNotFoundException>(() =>
            svc.DownloadCandidateCvAsync(owner, camp.Id, cid, default));
    }

    // (g-ter) GET cv: ứng viên của org khác → KeyNotFoundException (→404).
    [Fact]
    public async Task Download_cv_ngoai_org_nem_KeyNotFoundException()
    {
        using var tdb = new CampaignTestDb();
        var owner = Guid.NewGuid();
        var camp = SeedCampaign(tdb, owner);
        var cid = Guid.NewGuid();
        tdb.Db.CampaignCandidates.Add(new CampaignCandidate
        {
            Id = cid, CampaignId = camp.Id, CvFileUrl = "campaigns/x/candidates/y.pdf",
            ParseStatus = CvParseStatus.Done, Status = CandidateStatus.Filtered,
            CreatedAt = DateTime.UtcNow, UpdatedAt = DateTime.UtcNow
        });
        await tdb.Db.SaveChangesAsync();

        var svc = new CampaignSvc(tdb.NewContext(), Mock.Of<IFileService>(), Mock.Of<ILogger<CampaignSvc>>(),
            Mock.Of<IParserService>(), Mock.Of<ICriteriaSuggester>(), Mock.Of<IInvitationEmailPublisher>());

        await Assert.ThrowsAsync<KeyNotFoundException>(() =>
            svc.DownloadCandidateCvAsync(Guid.NewGuid() /* org khác */, camp.Id, cid, default));
    }
}
