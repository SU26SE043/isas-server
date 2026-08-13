using System.Globalization;
using System.Security.Claims;
using System.Text;
using CsvHelper;
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
/// (c) format lạ → 400 (BadRequest). (format=pdf: xem CampaignResultsPdfF16Tests — F16 đã hỗ trợ.)
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
    // ⚠ CHỈ dùng cho assert HEADER (1 dòng, không có ký tự cần escape). Dữ liệu PHẢI đọc bằng
    // ParseCsv: `Split(',')` sẽ cắt sai đúng ô có dấu phẩy/nháy — chính là lỗi F5 đi bịt.
    private static string[] CsvLines(byte[] bytes) =>
        Encoding.UTF8.GetString(bytes)
            .Replace("\r\n", "\n").Replace("\r", "\n")
            .Split('\n', StringSplitOptions.RemoveEmptyEntries);

    // Parse CSV THẬT bằng CsvHelper (đã là dependency) → đọc theo TÊN cột, không theo vị trí.
    // Quan trọng: giá trị có dấu phẩy/nháy được un-escape đúng, nên test bắt được lỗi escape.
    private static List<Dictionary<string, string>> ParseCsv(byte[] bytes)
    {
        using var reader = new StringReader(Encoding.UTF8.GetString(bytes));
        using var csv = new CsvReader(reader, CultureInfo.InvariantCulture);
        csv.Read();
        csv.ReadHeader();
        var header = csv.HeaderRecord!;
        var rows = new List<Dictionary<string, string>>();
        while (csv.Read())
            rows.Add(header.ToDictionary(h => h, h => csv.GetField(h) ?? string.Empty));
        return rows;
    }

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
        // Header = HỢP ĐỒNG với HR (script/Excel của họ bám tên + thứ tự cột) → so sánh CHÍNH XÁC,
        // không hạ xuống Contains. SEC-4 thêm `flags`; F5 thêm `full_name`,`email`; CAMP-18 thêm
        // `rubric_version` — đều ở CUỐI (additive: thứ tự cột cũ không đổi, script cũ đọc theo chỉ số
        // vẫn chạy). Chuỗi kỳ vọng đổi CÓ CHỦ ĐÍCH ở CAMP-18: thiếu cột thước đo thì HR xuất Excel
        // rồi trộn điểm của hai thước đo, hoàn toàn ngoài tầm mọi cảnh báo app hiện trên màn hình.
        Assert.Equal(
            "rank,candidate_id,session_id,total_score,result,scored_at,flags,full_name,email,rubric_version",
            lines[0]);

        var rows = ParseCsv(file.FileContents);
        Assert.Equal(expected.Results.Count, rows.Count);

        // Từng dòng khớp E5 theo THỨ TỰ, đọc theo TÊN cột (không phụ thuộc vị trí).
        for (int i = 0; i < expected.Results.Count; i++)
        {
            var r = rows[i];
            var e = expected.Results[i];
            Assert.Equal(e.Rank, int.Parse(r["rank"]));
            Assert.Equal(e.CandidateId, Guid.Parse(r["candidate_id"]));
            Assert.Equal(e.SessionId, Guid.Parse(r["session_id"]));
            Assert.Equal(e.TotalScore, decimal.Parse(r["total_score"], CultureInfo.InvariantCulture));
            Assert.Equal(e.Result ?? string.Empty, r["result"]);   // Pass/Fail; ngưỡng null → ""
        }

        // Rank + pass/fail cụ thể (đồng hạng 1,2,2,4).
        Assert.Equal(new[] { 1, 2, 2, 4 }, rows.Select(r => int.Parse(r["rank"])).ToArray());
        Assert.Equal(new[] { "Pass", "Pass", "Pass", "Fail" }, rows.Select(r => r["result"]).ToArray());
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
        var rows = ParseCsv(file.FileContents);
        Assert.Single(rows);
        Assert.Equal(string.Empty, rows[0]["result"]);   // result rỗng
    }

    // Seed membership (đường-1: không CV) mang sẵn danh tính snapshot.
    private static void SeedMembership(
        CampaignDbContext db, Guid campaignId, Guid candidateId,
        string? fullName, string? email, Guid? cvSubmissionId = null)
    {
        db.CampaignMemberships.Add(new CampaignMembership
        {
            Id = Guid.NewGuid(),
            CampaignId = campaignId,
            CandidateId = candidateId,
            CvSubmissionId = cvSubmissionId,
            FullName = fullName,
            Email = email,
            Status = MembershipStatus.Joined,
            JoinedAt = DateTime.UtcNow,
            CreatedAt = DateTime.UtcNow,
            UpdatedAt = DateTime.UtcNow
        });
        db.SaveChanges();
    }

    // ── F5 — họ tên + email trong CSV ────────────────────────────────────────────

    // (d) Tên chứa dấu phẩy VÀ dấu nháy → parse lại phải ra CHUỖI GỐC.
    // Đây là ca mà `Split(',')` của test cũ che mất: nó cắt ô làm đôi và vẫn "xanh".
    [Fact]
    public async Task Export_ten_co_dau_phay_va_nhay_khong_vo_file()
    {
        using var tdb = new CampaignTestDb();
        var orgId = Guid.NewGuid();
        var campaign = SeedCampaign(tdb.Db, orgId, passScorePct: 50);

        var r1 = SeedRanking(tdb.Db, campaign.Id, 90m, DateTime.UtcNow);
        var r2 = SeedRanking(tdb.Db, campaign.Id, 80m, DateTime.UtcNow.AddMinutes(1));
        SeedMembership(tdb.Db, campaign.Id, r1.CandidateId, "Nguyễn Văn A, Jr.", "a@x.co");
        SeedMembership(tdb.Db, campaign.Id, r2.CandidateId, "Trần \"Bíp\" B", "b@x.co");

        var result = await NewController(tdb.NewContext(), orgId)
            .ExportCampaignResults(campaign.Id, "csv", default);

        var file = Assert.IsType<FileContentResult>(result);
        var rows = ParseCsv(file.FileContents);

        Assert.Equal(2, rows.Count);
        Assert.Equal("Nguyễn Văn A, Jr.", rows[0]["full_name"]);
        Assert.Equal("a@x.co", rows[0]["email"]);
        Assert.Equal("Trần \"Bíp\" B", rows[1]["full_name"]);
        Assert.Equal("b@x.co", rows[1]["email"]);
    }

    // (e) Membership đường-1 lịch sử (không CV, chưa join lại sau F5) → 2 ô RỖNG, không throw.
    // Ô trống là kết quả ĐÚNG: migration cố ý không đoán danh tính đường-1 (xem comment migration).
    [Fact]
    public async Task Export_membership_duong1_khong_co_danh_tinh_tra_o_rong()
    {
        using var tdb = new CampaignTestDb();
        var orgId = Guid.NewGuid();
        var campaign = SeedCampaign(tdb.Db, orgId);

        var r = SeedRanking(tdb.Db, campaign.Id, 75m, DateTime.UtcNow);
        SeedMembership(tdb.Db, campaign.Id, r.CandidateId, fullName: null, email: null);

        var result = await NewController(tdb.NewContext(), orgId)
            .ExportCampaignResults(campaign.Id, "csv", default);

        var file = Assert.IsType<FileContentResult>(result);
        var rows = ParseCsv(file.FileContents);

        Assert.Single(rows);
        Assert.Equal(string.Empty, rows[0]["full_name"]);
        Assert.Equal(string.Empty, rows[0]["email"]);
    }

    // (e') Không có membership nào khớp candidate (ranking mồ côi) → vẫn xuất được, 2 ô rỗng.
    [Fact]
    public async Task Export_khong_co_membership_van_khong_throw()
    {
        using var tdb = new CampaignTestDb();
        var orgId = Guid.NewGuid();
        var campaign = SeedCampaign(tdb.Db, orgId);
        SeedRanking(tdb.Db, campaign.Id, 60m, DateTime.UtcNow);

        var result = await NewController(tdb.NewContext(), orgId)
            .ExportCampaignResults(campaign.Id, "csv", default);

        var file = Assert.IsType<FileContentResult>(result);
        var rows = ParseCsv(file.FileContents);
        Assert.Single(rows);
        Assert.Equal(string.Empty, rows[0]["full_name"]);
        Assert.Equal(string.Empty, rows[0]["email"]);
    }

    // (f) Fallback cv_submission: membership đường-2 CŨ (cột snapshot còn null vì backfill sót)
    // → vẫn ra tên/email nhờ LEFT JOIN ở GetIdentityByCandidateAsync.
    [Fact]
    public async Task Export_fallback_cv_submission_khi_snapshot_con_null()
    {
        using var tdb = new CampaignTestDb();
        var orgId = Guid.NewGuid();
        var campaign = SeedCampaign(tdb.Db, orgId);

        var cvId = Guid.NewGuid();
        tdb.Db.CvSubmissions.Add(new CvSubmission
        {
            Id = cvId,
            CampaignId = campaign.Id,
            FullName = "Lê Thị C",
            Email = "c@x.co",
            CreatedAt = DateTime.UtcNow,
            UpdatedAt = DateTime.UtcNow
        });
        tdb.Db.SaveChanges();

        var r = SeedRanking(tdb.Db, campaign.Id, 85m, DateTime.UtcNow);
        SeedMembership(tdb.Db, campaign.Id, r.CandidateId, fullName: null, email: null, cvSubmissionId: cvId);

        var result = await NewController(tdb.NewContext(), orgId)
            .ExportCampaignResults(campaign.Id, "csv", default);

        var file = Assert.IsType<FileContentResult>(result);
        var rows = ParseCsv(file.FileContents);
        Assert.Single(rows);
        Assert.Equal("Lê Thị C", rows[0]["full_name"]);
        Assert.Equal("c@x.co", rows[0]["email"]);
    }

    // (g) JSON (E5) và CSV (E6) dùng CHUNG một chỗ sửa → danh tính phải xuất hiện ở cả hai.
    [Fact]
    public async Task Ket_qua_JSON_cung_mang_danh_tinh()
    {
        using var tdb = new CampaignTestDb();
        var orgId = Guid.NewGuid();
        var campaign = SeedCampaign(tdb.Db, orgId);

        var r = SeedRanking(tdb.Db, campaign.Id, 88m, DateTime.UtcNow);
        SeedMembership(tdb.Db, campaign.Id, r.CandidateId, "Phạm D", "d@x.co");

        var res = await NewService(tdb.NewContext())
            .GetCampaignResultsAsync(orgId, campaign.Id, default);

        Assert.Equal("Phạm D", res.Results[0].FullName);
        Assert.Equal("d@x.co", res.Results[0].Email);
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

    // (c) format lạ → 400.
    // ⚠ F16 ĐỔI TIỀN ĐỀ CÓ CHỦ ĐÍCH: "pdf" trước đây nằm trong bộ InlineData này vì E6 hoãn PDF
    // (rủi ro native SkiaSharp — backlog BK8). Nay PDF đã hỗ trợ nên ca "pdf" chuyển sang
    // CampaignResultsPdfF16Tests. KHÔNG phải nới assert: hành vi "format lạ → 400" vẫn bị khoá
    // nguyên vẹn bằng xlsx/docx.
    [Theory]
    [InlineData("xlsx")]
    [InlineData("docx")]
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
