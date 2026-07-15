using System.Security.Claims;
using System.Text;
using Isas.CampaignService.Controllers;
using Isas.CampaignService.Models;
using Isas.CampaignService.Services;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc;
using Microsoft.Extensions.Logging;
using Moq;

// 'CampaignService' vừa là namespace vừa là tên class → alias cho rõ ràng.
using CampaignSvc = Isas.CampaignService.Services.CampaignService;

namespace Isas.CampaignService.Tests;

/// <summary>
/// E6 — GET /campaign/{id}/results/export?format=csv: xuất bảng kết quả (E5) ra file.
/// Test ở tầng controller (biên HTTP) để chứng minh:
/// (a) format=csv → Content-Type "text/csv" + NỘI DUNG khớp E5 (thứ tự + rank + total_score + pass/fail);
/// (a') thiếu format → mặc định csv;
/// (b) người ngoài org → 404 (NotFound);
/// (c) format=pdf (chưa hỗ trợ) / format lạ → 400 (BadRequest).
/// </summary>
public class CampaignResultsExportTests
{
    private static CampaignSvc NewService(CampaignDbContext db) =>
        new(db, Mock.Of<IFileService>(), Mock.Of<ILogger<CampaignSvc>>(),
            Mock.Of<IParserService>(), Mock.Of<ICriteriaSuggester>(),
            Mock.Of<IInvitationEmailPublisher>());

    // BK4: Controller lấy owner từ claim `org_id` (ownership theo ORG). NameIdentifier = actor (audit).
    private static CampaignController NewController(CampaignDbContext db, Guid orgId)
    {
        var controller = new CampaignController(
            NewService(db), Mock.Of<ICvScreeningService>(), Mock.Of<ILogger<CampaignController>>());
        var identity = new ClaimsIdentity(
            new[]
            {
                new Claim("org_id", orgId.ToString()),
                new Claim(ClaimTypes.NameIdentifier, Guid.NewGuid().ToString()),   // HR cá nhân (khác org)
            }, "Test");
        controller.ControllerContext = new ControllerContext
        {
            HttpContext = new DefaultHttpContext { User = new ClaimsPrincipal(identity) }
        };
        return controller;
    }

    private static Campaign SeedCampaign(CampaignDbContext db, Guid orgId, int? passScorePct = null)
    {
        var c = CampaignTestDb.NewCampaign(orgId, CampaignStatus.Active);
        c.PassScorePct = passScorePct;
        db.Campaigns.Add(c);
        db.SaveChanges();
        return c;
    }

    private static CampaignRanking SeedRanking(
        CampaignDbContext db, Guid campaignId, decimal score, DateTime scoredAt)
    {
        var r = new CampaignRanking
        {
            Id = Guid.NewGuid(),
            CampaignId = campaignId,
            CandidateId = Guid.NewGuid(),
            SessionId = Guid.NewGuid(),
            TotalScore = score,
            UpdatedAt = scoredAt
        };
        db.CampaignRankings.Add(r);
        db.SaveChanges();
        return r;
    }

    // Tách CSV thành các dòng (bỏ dòng rỗng cuối), chuẩn hoá CRLF/CR.
    private static string[] CsvLines(byte[] bytes) =>
        Encoding.UTF8.GetString(bytes)
            .Replace("\r\n", "\n").Replace("\r", "\n")
            .Split('\n', StringSplitOptions.RemoveEmptyEntries);

    // (a) format=csv → text/csv + nội dung khớp E5 (thứ tự + rank + total_score + pass/fail).
    [Fact]
    public async Task Export_csv_content_type_va_noi_dung_khop_E5()
    {
        using var tdb = new CampaignTestDb();
        var orgId = Guid.NewGuid();
        var campaign = SeedCampaign(tdb.Db, orgId, passScorePct: 80);

        var t0 = DateTime.UtcNow;
        SeedRanking(tdb.Db, campaign.Id, 90.00m, t0);                 // rank 1 → Pass
        SeedRanking(tdb.Db, campaign.Id, 82.50m, t0.AddMinutes(1));   // rank 2 → Pass
        SeedRanking(tdb.Db, campaign.Id, 82.50m, t0.AddMinutes(2));   // rank 2 (đồng hạng) → Pass
        SeedRanking(tdb.Db, campaign.Id, 70.00m, t0.AddMinutes(3));   // rank 4 → Fail

        // Nguồn sự thật: E5 (tính độc lập) để so từng dòng — export PHẢI khớp y hệt.
        var expected = await NewService(tdb.NewContext())
            .GetCampaignResultsAsync(orgId, campaign.Id, default);

        var result = await NewController(tdb.NewContext(), orgId)
            .ExportCampaignResults(campaign.Id, "csv", default);

        var file = Assert.IsType<FileContentResult>(result);
        Assert.Equal("text/csv", file.ContentType);
        Assert.EndsWith(".csv", file.FileDownloadName);

        var lines = CsvLines(file.FileContents);
        // Header snake_case + đúng số dòng dữ liệu (= số ứng viên E5). SEC-4: thêm cột `flags` cuối (additive).
        Assert.Equal("rank,candidate_id,session_id,total_score,result,scored_at,flags", lines[0]);
        Assert.Equal(expected.Results.Count, lines.Length - 1);

        // Từng dòng khớp E5 theo THỨ TỰ: rank · candidate_id · total_score · result.
        for (int i = 0; i < expected.Results.Count; i++)
        {
            var cols = lines[i + 1].Split(',');
            var e = expected.Results[i];
            Assert.Equal(e.Rank, int.Parse(cols[0]));
            Assert.Equal(e.CandidateId, Guid.Parse(cols[1]));
            Assert.Equal(e.SessionId, Guid.Parse(cols[2]));
            Assert.Equal(e.TotalScore, decimal.Parse(cols[3], System.Globalization.CultureInfo.InvariantCulture));
            Assert.Equal(e.Result ?? string.Empty, cols[4]);   // Pass/Fail; ngưỡng null → ""
        }

        // Rank + pass/fail cụ thể (đồng hạng 1,2,2,4).
        Assert.Equal(new[] { 1, 2, 2, 4 }, lines.Skip(1).Select(l => int.Parse(l.Split(',')[0])).ToArray());
        Assert.Equal(new[] { "Pass", "Pass", "Pass", "Fail" },
            lines.Skip(1).Select(l => l.Split(',')[4]).ToArray());
    }

    // (a') Thiếu format → mặc định csv (không lỗi).
    [Fact]
    public async Task Export_thieu_format_mac_dinh_csv()
    {
        using var tdb = new CampaignTestDb();
        var orgId = Guid.NewGuid();
        var campaign = SeedCampaign(tdb.Db, orgId);
        SeedRanking(tdb.Db, campaign.Id, 88.00m, DateTime.UtcNow);

        var result = await NewController(tdb.NewContext(), orgId)
            .ExportCampaignResults(campaign.Id, null, default);

        var file = Assert.IsType<FileContentResult>(result);
        Assert.Equal("text/csv", file.ContentType);
    }

    // (a'') Ngưỡng null → cột result rỗng (HR quyết tay) — khớp E5.
    [Fact]
    public async Task Export_nguong_null_thi_cot_result_rong()
    {
        using var tdb = new CampaignTestDb();
        var orgId = Guid.NewGuid();
        var campaign = SeedCampaign(tdb.Db, orgId, passScorePct: null);
        SeedRanking(tdb.Db, campaign.Id, 95.00m, DateTime.UtcNow);

        var result = await NewController(tdb.NewContext(), orgId)
            .ExportCampaignResults(campaign.Id, "csv", default);

        var file = Assert.IsType<FileContentResult>(result);
        var lines = CsvLines(file.FileContents);
        Assert.Equal(2, lines.Length);                       // header + 1 dòng
        Assert.Equal(string.Empty, lines[1].Split(',')[4]);  // result rỗng
    }

    // (b) Người ngoài org → 404 (KeyNotFoundException từ E5 → NotFound).
    [Fact]
    public async Task Export_nguoi_ngoai_org_tra_404()
    {
        using var tdb = new CampaignTestDb();
        var ownerId = Guid.NewGuid();
        var outsiderId = Guid.NewGuid();
        var campaign = SeedCampaign(tdb.Db, ownerId);
        SeedRanking(tdb.Db, campaign.Id, 88.00m, DateTime.UtcNow);

        var result = await NewController(tdb.NewContext(), outsiderId)
            .ExportCampaignResults(campaign.Id, "csv", default);

        Assert.IsType<NotFoundObjectResult>(result);
    }

    // (c) format=pdf (chưa hỗ trợ) → 400; format lạ → 400.
    [Theory]
    [InlineData("pdf")]
    [InlineData("xlsx")]
    public async Task Export_format_khong_ho_tro_tra_400(string format)
    {
        using var tdb = new CampaignTestDb();
        var orgId = Guid.NewGuid();
        var campaign = SeedCampaign(tdb.Db, orgId);
        SeedRanking(tdb.Db, campaign.Id, 88.00m, DateTime.UtcNow);

        var result = await NewController(tdb.NewContext(), orgId)
            .ExportCampaignResults(campaign.Id, format, default);

        Assert.IsType<BadRequestObjectResult>(result);
    }
}
