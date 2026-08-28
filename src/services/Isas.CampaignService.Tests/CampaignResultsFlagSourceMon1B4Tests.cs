using System.Globalization;
using System.Text.Json;
using CsvHelper;
using Isas.CampaignService.DTOs;
using Isas.CampaignService.Models;
using Isas.CampaignService.Services;
using Microsoft.Extensions.Logging;
using Moq;
using UglyToad.PdfPig;

using CampaignSvc = Isas.CampaignService.Services.CampaignService;

namespace Isas.CampaignService.Tests;

/// <summary>
/// MON1-B4 — lộ `source` (B1 <c>session_flags.source</c>) ra màn HR.
/// HỢP ĐỒNG CHUNG MON1 — hash d480fe84152a5417:
///  • trường "source" trên MỖI phần tử flags[], string, KHÔNG null, mặc định "Client";
///  • xuất hiện ở CẢ results[].flags[] LẪN unscoredFlagged[].flags[];
///  • một signal_type có cả Client lẫn Server ⇒ TÁCH hai FlagDto, không gộp;
///  • CSV/PDF export thêm source, dùng chung nguồn (F16 — hai bản xuất không lệch).
/// </summary>
public class CampaignResultsFlagSourceMon1B4Tests
{
    private static CampaignSvc NewService(CampaignDbContext db) =>
        new(db, Mock.Of<IFileService>(), Mock.Of<ILogger<CampaignSvc>>(),
            Mock.Of<IParserService>(), Mock.Of<ICriteriaSuggester>(),
            Mock.Of<IInvitationEmailPublisher>());

    private static Campaign SeedCampaign(CampaignDbContext db, Guid orgId)
    {
        var c = CampaignTestDb.NewCampaign(orgId, CampaignStatus.Active);
        db.Campaigns.Add(c);
        db.SaveChanges();
        return c;
    }

    private static void SeedRanking(CampaignDbContext db, Guid campaignId, Guid candidateId, Guid sessionId, decimal score)
    {
        db.CampaignRankings.Add(new CampaignRanking
        {
            Id = Guid.NewGuid(),
            CampaignId = campaignId,
            CandidateId = candidateId,
            SessionId = sessionId,
            TotalScore = score,
            UpdatedAt = DateTime.UtcNow
        });
        db.SaveChanges();
    }

    // source: null = KHÔNG set (dựa entity default B1) ; ngược lại set tường minh.
    private static void SeedFlag(
        CampaignDbContext db, Guid campaignId, Guid sessionId, Guid candidateId,
        string signalType, FlagSource? source = FlagSource.Client, string? note = null)
    {
        var f = new SessionFlag
        {
            Id = Guid.NewGuid(),
            CampaignId = campaignId,
            SessionId = sessionId,
            CandidateId = candidateId,
            SignalType = signalType,
            Note = note,
            DetectedAt = DateTime.UtcNow
        };
        if (source is not null) f.Source = source.Value;
        db.SessionFlags.Add(f);
        db.SaveChanges();
    }

    // ── source có mặt ở CẢ HAI nhánh + chỉ thuộc {Client, Server} ────────────────────────────────
    [Fact]
    public async Task Response_flags_co_source_o_ca_hai_nhanh()
    {
        using var tdb = new CampaignTestDb();
        var orgId = Guid.NewGuid();
        var camp = SeedCampaign(tdb.Db, orgId);

        var cScored = Guid.NewGuid(); var sScored = Guid.NewGuid();
        SeedRanking(tdb.Db, camp.Id, cScored, sScored, 70m);
        SeedFlag(tdb.Db, camp.Id, sScored, cScored, "paste", FlagSource.Client);

        var cUnscored = Guid.NewGuid(); var sUnscored = Guid.NewGuid();
        SeedFlag(tdb.Db, camp.Id, sUnscored, cUnscored, "monitoring_gap", FlagSource.Server);

        var res = await NewService(tdb.NewContext()).GetCampaignResultsAsync(orgId, camp.Id, default);

        var scoredFlag = Assert.Single(Assert.Single(res.Results).Flags);
        Assert.Equal("Client", scoredFlag.Source);

        var unscoredFlag = Assert.Single(Assert.Single(res.UnscoredFlagged).Flags);
        Assert.Equal("Server", unscoredFlag.Source);

        // Mọi source chỉ thuộc {Client, Server}.
        var allSources = res.Results.SelectMany(r => r.Flags).Select(f => f.Source)
            .Concat(res.UnscoredFlagged.SelectMany(u => u.Flags).Select(f => f.Source));
        Assert.All(allSources, s => Assert.Contains(s, new[] { "Client", "Server" }));
    }

    // ── dòng cũ (không set source) → "Client" ───────────────────────────────────────────────────
    [Fact]
    public async Task Dong_cu_default_ra_Client()
    {
        using var tdb = new CampaignTestDb();
        var orgId = Guid.NewGuid();
        var camp = SeedCampaign(tdb.Db, orgId);
        var c = Guid.NewGuid(); var s = Guid.NewGuid();
        SeedRanking(tdb.Db, camp.Id, c, s, 55m);
        SeedFlag(tdb.Db, camp.Id, s, c, "tab_switch", source: null);   // KHÔNG set → entity default

        var res = await NewService(tdb.NewContext()).GetCampaignResultsAsync(orgId, camp.Id, default);

        Assert.Equal("Client", Assert.Single(Assert.Single(res.Results).Flags).Source);
    }

    // ── cùng signal_type có cả Client lẫn Server → TÁCH hai mục, KHÔNG gộp ───────────────────────
    [Fact]
    public async Task Client_va_Server_cung_signal_tach_hai_muc()
    {
        using var tdb = new CampaignTestDb();
        var orgId = Guid.NewGuid();
        var camp = SeedCampaign(tdb.Db, orgId);
        var c = Guid.NewGuid(); var s = Guid.NewGuid();
        SeedRanking(tdb.Db, camp.Id, c, s, 65m);
        SeedFlag(tdb.Db, camp.Id, s, c, "monitoring_gap", FlagSource.Client);
        SeedFlag(tdb.Db, camp.Id, s, c, "monitoring_gap", FlagSource.Client);
        SeedFlag(tdb.Db, camp.Id, s, c, "monitoring_gap", FlagSource.Client);
        SeedFlag(tdb.Db, camp.Id, s, c, "monitoring_gap", FlagSource.Server);

        var res = await NewService(tdb.NewContext()).GetCampaignResultsAsync(orgId, camp.Id, default);
        var flags = Assert.Single(res.Results).Flags;

        Assert.Equal(2, flags.Count);                                        // TÁCH, không gộp thành 1×4
        var client = Assert.Single(flags, f => f.Source == "Client");
        var server = Assert.Single(flags, f => f.Source == "Server");
        Assert.Equal("monitoring_gap", client.Type);
        Assert.Equal("monitoring_gap", server.Type);
        Assert.Equal(3, client.Count);
        Assert.Equal(1, server.Count);
        Assert.DoesNotContain(flags, f => f.Count == 4);                     // KHÔNG có mục gộp
        // Tie-break: Client trước Server (deterministic).
        Assert.Equal("Client", flags[0].Source);
        Assert.Equal("Server", flags[1].Source);
    }

    // ── nhánh unscoredFlagged CŨNG tách + có source (không phụ thuộc ranking) ────────────────────
    [Fact]
    public async Task UnscoredFlagged_cung_tach_va_co_source()
    {
        using var tdb = new CampaignTestDb();
        var orgId = Guid.NewGuid();
        var camp = SeedCampaign(tdb.Db, orgId);
        var c = Guid.NewGuid(); var s = Guid.NewGuid();   // KHÔNG SeedRanking → chưa Scored
        SeedFlag(tdb.Db, camp.Id, s, c, "monitoring_gap", FlagSource.Client);
        SeedFlag(tdb.Db, camp.Id, s, c, "monitoring_gap", FlagSource.Server);

        var res = await NewService(tdb.NewContext()).GetCampaignResultsAsync(orgId, camp.Id, default);
        var flags = Assert.Single(res.UnscoredFlagged).Flags;

        Assert.Equal(2, flags.Count);
        Assert.Equal("Client", Assert.Single(flags, f => f.Source == "Client").Source);
        Assert.Equal("Server", Assert.Single(flags, f => f.Source == "Server").Source);
    }

    // ── CSV ≡ PDF (F16) — source vào CẢ HAI bản xuất, giống hệt ─────────────────────────────────
    [Fact]
    public async Task CSV_va_PDF_flags_khong_lech()
    {
        using var tdb = new CampaignTestDb();
        var orgId = Guid.NewGuid();
        var camp = SeedCampaign(tdb.Db, orgId);

        var c = Guid.NewGuid(); var s = Guid.NewGuid();
        SeedRanking(tdb.Db, camp.Id, c, s, 80m);
        SeedFlag(tdb.Db, camp.Id, s, c, "paste", FlagSource.Client);
        SeedFlag(tdb.Db, camp.Id, s, c, "paste", FlagSource.Client);
        SeedFlag(tdb.Db, camp.Id, s, c, "monitoring_gap", FlagSource.Server);

        var svc = NewService(tdb.NewContext());
        var csv = await svc.ExportCampaignResultsAsync(orgId, camp.Id, "csv", default);
        var pdf = await NewService(tdb.NewContext()).ExportCampaignResultsAsync(orgId, camp.Id, "pdf", default);

        // So khi ĐÃ BỎ MỌI KHOẢNG TRẮNG: PdfPig ngắt dòng cột flags hẹp ở vị trí không ổn định (chèn
        // space/newline giữa token) — không phải khác biệt nội dung. F16 = CSV và PDF cùng NỘI DUNG.
        static string NoWs(string s) => new(s.Where(c => !char.IsWhiteSpace(c)).ToArray());
        var csvFlags = NoWs(Assert.Single(ParseCsv(csv.Content))["flags"]);
        var pdfText = NoWs(ExtractPdfText(pdf.Content));

        // Cùng chuỗi tóm tắt "type(source):count" phải có trong CẢ HAI — source vào cả hai bản xuất.
        foreach (var token in new[] { "paste(Client):2", "monitoring_gap(Server):1" })
        {
            Assert.Contains(token, csvFlags);
            Assert.Contains(token, pdfText);
        }
    }

    // ── DTO: "source" serialize camelCase, Ở CUỐI (sau lastAt), giá trị "Client" ─────────────────
    [Fact]
    public void FlagDto_source_serialize_camelCase_o_cuoi()
    {
        var json = JsonSerializer.Serialize(new FlagDto { Type = "X", Count = 1 },
            new JsonSerializerOptions(JsonSerializerDefaults.Web));

        Assert.Contains("\"source\"", json);
        Assert.DoesNotContain("\"Source\"", json);
        Assert.Contains("\"source\":\"Client\"", json);
        // Additive Ở CUỐI: source đứng SAU lastAt (client cũ đọc tuần tự không lệch).
        Assert.True(json.IndexOf("\"lastAt\"", StringComparison.Ordinal)
                  < json.IndexOf("\"source\"", StringComparison.Ordinal));
    }

    // ── helper: default FlagDto.Source = "Client" (contract literal, không phải null) ────────────
    [Fact]
    public void FlagDto_default_source_la_Client()
    {
        Assert.Equal("Client", new FlagDto().Source);
    }

    private static List<Dictionary<string, string>> ParseCsv(byte[] bytes)
    {
        using var reader = new StringReader(System.Text.Encoding.UTF8.GetString(bytes));
        using var csv = new CsvReader(reader, CultureInfo.InvariantCulture);
        csv.Read();
        csv.ReadHeader();
        var header = csv.HeaderRecord!;
        var rows = new List<Dictionary<string, string>>();
        while (csv.Read())
            rows.Add(header.ToDictionary(h => h, h => csv.GetField(h) ?? string.Empty));
        return rows;
    }

    // Mẫu F16 (CampaignResultsPdfF16Tests.PdfText): raw page text, gộp khoảng trắng.
    private static string ExtractPdfText(byte[] bytes)
    {
        using var doc = PdfDocument.Open(bytes);
        var raw = string.Join(" ", doc.GetPages().Select(p => p.Text));
        return string.Join(" ", raw.Split((char[]?)null, StringSplitOptions.RemoveEmptyEntries));
    }
}
