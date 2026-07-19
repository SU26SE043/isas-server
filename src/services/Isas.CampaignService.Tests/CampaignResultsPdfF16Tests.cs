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
using UglyToad.PdfPig;

using CampaignSvc = Isas.CampaignService.Services.CampaignService;

namespace Isas.CampaignService.Tests;

/// <summary>
/// F16 (FR14, gộp BK8) — `GET /campaign/{id}/results/export?format=pdf`.
///
/// Trước F16, "pdf" → 400 (E6 hoãn vì rủi ro native SkiaSharp). Nay hỗ trợ bằng QuestPDF +
/// `SkiaSharp.NativeAssets.Linux.NoDependencies` (không cần cài lib hệ thống trong container).
///
/// Yêu cầu cốt lõi của task là **PDF khớp số liệu CSV** — nên test KHÔNG chỉ kiểm "trả về vài byte":
/// nó sinh CẢ HAI bản xuất từ cùng dữ liệu, **đọc chữ ra khỏi PDF thật bằng PdfPig** (đã là dependency
/// của Isas.Shared) rồi đối chiếu từng con số với CSV. Nếu hai đường serialize trôi khỏi nhau thì đỏ.
///
/// (a) format=pdf → FileContentResult, content-type application/pdf, byte thật là PDF;
/// (b) mọi hàng CSV (hạng · điểm · kết quả · tên · email) đều có mặt trong PDF, đúng THỨ TỰ hạng;
/// (c) tên tiếng Việt có dấu không bị mất glyph (QuestPDF ném khi font thiếu glyph);
/// (d) ngoài org → 404 (không rò rỉ, giống nhánh csv);
/// (e) chưa ai được chấm → vẫn ra PDF hợp lệ (không nổ trên bảng rỗng);
/// (f) ngưỡng pass null → cột kết quả rỗng, khớp CSV.
/// </summary>
public class CampaignResultsPdfF16Tests
{
    private static CampaignSvc NewService(CampaignDbContext db) =>
        new(db, Mock.Of<IFileService>(), Mock.Of<ILogger<CampaignSvc>>(),
            Mock.Of<IParserService>(), Mock.Of<ICriteriaSuggester>(),
            Mock.Of<IInvitationEmailPublisher>());

    private static CampaignController NewController(CampaignDbContext db, Guid orgId)
    {
        var controller = new CampaignController(
            NewService(db), Mock.Of<ICvScreeningService>(), Mock.Of<ILogger<CampaignController>>());
        var identity = new ClaimsIdentity(
            new[]
            {
                new Claim("org_id", orgId.ToString()),
                new Claim(ClaimTypes.NameIdentifier, Guid.NewGuid().ToString()),
            }, "Test");
        controller.ControllerContext = new ControllerContext
        {
            HttpContext = new DefaultHttpContext { User = new ClaimsPrincipal(identity) }
        };
        return controller;
    }

    private static Campaign SeedCampaign(CampaignDbContext db, Guid orgId, int? passScorePct = 60)
    {
        var c = CampaignTestDb.NewCampaign(orgId, CampaignStatus.Active);
        c.Title = "Tuyển Backend .NET — đợt tháng 8";
        c.PassScorePct = passScorePct;
        db.Campaigns.Add(c);
        db.SaveChanges();
        return c;
    }

    /// <summary>Seed 1 ứng viên đã chấm + snapshot danh tính (để PDF/CSV có tên & email).</summary>
    private static void SeedScoredCandidate(
        CampaignDbContext db, Guid campaignId, decimal score, DateTime scoredAt,
        string fullName, string email)
    {
        var candidateId = Guid.NewGuid();
        db.CampaignRankings.Add(new CampaignRanking
        {
            Id = Guid.NewGuid(),
            CampaignId = campaignId,
            CandidateId = candidateId,
            SessionId = Guid.NewGuid(),
            TotalScore = score,
            UpdatedAt = scoredAt
        });
        db.CampaignMemberships.Add(new CampaignMembership
        {
            Id = Guid.NewGuid(),
            CampaignId = campaignId,
            CandidateId = candidateId,
            FullName = fullName,
            Email = email,
            Status = MembershipStatus.Joined,
            JoinedAt = scoredAt,
            CreatedAt = scoredAt,
            UpdatedAt = scoredAt
        });
        db.SaveChanges();
    }

    /// <summary>Đọc chữ ra khỏi PDF THẬT (PdfPig) — gộp mọi trang, nén khoảng trắng để so khớp ổn định.</summary>
    private static string PdfText(byte[] bytes)
    {
        using var doc = PdfDocument.Open(bytes);
        var raw = string.Join(" ", doc.GetPages().Select(p => p.Text));
        return string.Join(" ", raw.Split((char[]?)null, StringSplitOptions.RemoveEmptyEntries));
    }

    private static List<Dictionary<string, string>> ParseCsv(byte[] bytes)
    {
        using var reader = new StringReader(Encoding.UTF8.GetString(bytes));
        using var csv = new CsvReader(reader, CultureInfo.InvariantCulture);
        csv.Read();
        csv.ReadHeader();
        var rows = new List<Dictionary<string, string>>();
        while (csv.Read())
        {
            var row = new Dictionary<string, string>();
            foreach (var h in csv.HeaderRecord!)
                row[h] = csv.GetField(h) ?? string.Empty;
            rows.Add(row);
        }
        return rows;
    }

    private static async Task<byte[]> ExportAsync(CampaignTestDb tdb, Guid orgId, Guid campaignId, string format)
    {
        var result = await NewController(tdb.NewContext(), orgId)
            .ExportCampaignResults(campaignId, format, default);
        var file = Assert.IsType<FileContentResult>(result);
        return file.FileContents;
    }

    // ───────────────────── (a) trả về PDF thật ─────────────────────

    [Fact]
    public async Task Export_format_pdf_tra_ve_file_pdf()
    {
        using var tdb = new CampaignTestDb();
        var orgId = Guid.NewGuid();
        var campaign = SeedCampaign(tdb.Db, orgId);
        SeedScoredCandidate(tdb.Db, campaign.Id, 88.5m, DateTime.UtcNow, "Trần Minh Đức", "duc@example.com");

        var result = await NewController(tdb.NewContext(), orgId)
            .ExportCampaignResults(campaign.Id, "pdf", default);

        var file = Assert.IsType<FileContentResult>(result);
        Assert.Equal("application/pdf", file.ContentType);
        Assert.EndsWith(".pdf", file.FileDownloadName);
        // Byte đầu phải là chữ ký %PDF- — chứng minh là PDF thật, không phải chuỗi bịa.
        Assert.Equal("%PDF-", Encoding.ASCII.GetString(file.FileContents[..5]));
        // Mở được bằng parser độc lập = file không hỏng.
        Assert.Contains("Trần Minh Đức", PdfText(file.FileContents));
    }

    // ───────────────────── (b) khớp số liệu CSV ─────────────────────

    [Fact]
    public async Task Moi_hang_trong_CSV_deu_co_mat_trong_PDF()
    {
        using var tdb = new CampaignTestDb();
        var orgId = Guid.NewGuid();
        var campaign = SeedCampaign(tdb.Db, orgId, passScorePct: 60);
        var t0 = new DateTime(2026, 7, 10, 8, 0, 0, DateTimeKind.Utc);
        SeedScoredCandidate(tdb.Db, campaign.Id, 91.5m, t0, "Nguyễn Thị Hoa", "hoa@example.com");
        SeedScoredCandidate(tdb.Db, campaign.Id, 72.0m, t0.AddMinutes(5), "Lê Văn Bình", "binh@example.com");
        SeedScoredCandidate(tdb.Db, campaign.Id, 45.0m, t0.AddMinutes(9), "Phạm Quốc Anh", "anh@example.com");

        var csvRows = ParseCsv(await ExportAsync(tdb, orgId, campaign.Id, "csv"));
        var pdfText = PdfText(await ExportAsync(tdb, orgId, campaign.Id, "pdf"));

        Assert.Equal(3, csvRows.Count);
        foreach (var row in csvRows)
        {
            Assert.Contains(row["full_name"], pdfText);
            Assert.Contains(row["email"], pdfText);
            Assert.Contains(row["result"], pdfText);          // Pass / Fail
            // total_score CSV là "91.5"/"72.0"; PDF in "0.##" → "91.5"/"72". So bằng giá trị số.
            var score = decimal.Parse(row["total_score"], CultureInfo.InvariantCulture);
            Assert.Contains(score.ToString("0.##", CultureInfo.InvariantCulture), pdfText);
        }
    }

    [Fact]
    public async Task Thu_tu_hang_trong_PDF_giong_het_CSV()
    {
        using var tdb = new CampaignTestDb();
        var orgId = Guid.NewGuid();
        var campaign = SeedCampaign(tdb.Db, orgId);
        var t0 = new DateTime(2026, 7, 10, 8, 0, 0, DateTimeKind.Utc);
        // Seed theo thứ tự LỘN XỘN → nếu PDF không dùng đúng bảng đã sắp của E5 thì đỏ.
        SeedScoredCandidate(tdb.Db, campaign.Id, 45.0m, t0, "Ứng viên Thấp", "low@example.com");
        SeedScoredCandidate(tdb.Db, campaign.Id, 91.5m, t0.AddMinutes(1), "Ứng viên Cao", "high@example.com");
        SeedScoredCandidate(tdb.Db, campaign.Id, 72.0m, t0.AddMinutes(2), "Ứng viên Giữa", "mid@example.com");

        var csvRows = ParseCsv(await ExportAsync(tdb, orgId, campaign.Id, "csv"));
        var pdfText = PdfText(await ExportAsync(tdb, orgId, campaign.Id, "pdf"));

        var positions = csvRows.Select(r => pdfText.IndexOf(r["full_name"], StringComparison.Ordinal)).ToList();
        Assert.DoesNotContain(-1, positions);
        // Shortlist mà đảo thứ tự là sai nghiệp vụ, không phải sai thẩm mỹ: HR đọc từ trên xuống.
        Assert.Equal(positions.OrderBy(p => p).ToList(), positions);
    }

    // ───────────────────── (c) tiếng Việt không mất dấu ─────────────────────

    [Fact]
    public async Task Ten_tieng_Viet_co_dau_khong_bi_mat_glyph()
    {
        using var tdb = new CampaignTestDb();
        var orgId = Guid.NewGuid();
        var campaign = SeedCampaign(tdb.Db, orgId);
        // Gom các dấu khó: ĩ ữ ạ ệ ọ ườ ẫ đ Đ.
        const string name = "Đặng Hữu Nghĩa Trường";
        SeedScoredCandidate(tdb.Db, campaign.Id, 80m, DateTime.UtcNow, name, "nghia@example.com");

        // QuestPDF ném khi font thiếu glyph (Settings.CheckIfAllTextGlyphsAreAvailable mặc định bật)
        // → không nổ ở đây nghĩa là font đang dùng CÓ phủ tiếng Việt, không phải "in ra ô vuông".
        var pdfText = PdfText(await ExportAsync(tdb, orgId, campaign.Id, "pdf"));
        Assert.Contains(name, pdfText);
    }

    // ───────────────────── (d)(e)(f) biên ─────────────────────

    [Fact]
    public async Task Nguoi_ngoai_org_xuat_pdf_thi_404()
    {
        using var tdb = new CampaignTestDb();
        var orgId = Guid.NewGuid();
        var campaign = SeedCampaign(tdb.Db, orgId);
        SeedScoredCandidate(tdb.Db, campaign.Id, 88m, DateTime.UtcNow, "A", "a@example.com");

        var result = await NewController(tdb.NewContext(), Guid.NewGuid())
            .ExportCampaignResults(campaign.Id, "pdf", default);

        Assert.IsType<NotFoundObjectResult>(result);
    }

    [Fact]
    public async Task Chua_ai_duoc_cham_van_ra_PDF_hop_le()
    {
        using var tdb = new CampaignTestDb();
        var orgId = Guid.NewGuid();
        var campaign = SeedCampaign(tdb.Db, orgId);

        var bytes = await ExportAsync(tdb, orgId, campaign.Id, "pdf");

        Assert.Equal("%PDF-", Encoding.ASCII.GetString(bytes[..5]));
        // Vẫn phải đọc được + có tiêu đề chiến dịch (HR mở ra biết là báo cáo rỗng, không phải file hỏng).
        Assert.Contains("Tuyển Backend .NET", PdfText(bytes));
    }

    [Fact]
    public async Task Nguong_null_thi_cot_ket_qua_rong_giong_CSV()
    {
        using var tdb = new CampaignTestDb();
        var orgId = Guid.NewGuid();
        var campaign = SeedCampaign(tdb.Db, orgId, passScorePct: null);
        SeedScoredCandidate(tdb.Db, campaign.Id, 95m, DateTime.UtcNow, "Vũ Hải Yến", "yen@example.com");

        var csvRows = ParseCsv(await ExportAsync(tdb, orgId, campaign.Id, "csv"));
        var pdfText = PdfText(await ExportAsync(tdb, orgId, campaign.Id, "pdf"));

        Assert.Equal(string.Empty, Assert.Single(csvRows)["result"]);
        // Ngưỡng chưa đặt → PDF KHÔNG được tự phán Pass/Fail (HR quyết tay — E5/CAMP-11).
        Assert.DoesNotContain("Pass", pdfText);
        Assert.DoesNotContain("Fail", pdfText);
        Assert.Contains("Vũ Hải Yến", pdfText);
    }
}
