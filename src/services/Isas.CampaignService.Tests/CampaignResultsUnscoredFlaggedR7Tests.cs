using System.Globalization;
using CsvHelper;
using Isas.CampaignService.Models;
using Isas.CampaignService.Services;
using Microsoft.Extensions.Logging;
using Moq;
using UglyToad.PdfPig;

using CampaignSvc = Isas.CampaignService.Services.CampaignService;

namespace Isas.CampaignService.Tests;

/// <summary>
/// R7 — cờ chống gian lận của ứng viên CHƯA `Scored` (bỏ ngang / đang thi) phải tới được HR.
/// Trước fix: results/CSV/PDF chỉ liệt kê ứng viên có row `campaign_rankings` (Scored) → session bỏ ngang
/// dù đầy cờ trong `session_flags` vẫn `totalCandidates:0, results:[]` — nhóm đáng ngờ NHẤT bị giấu.
/// Sau fix: những session có cờ mà không có ranking lộ ra ở `UnscoredFlagged` (JSON) + nối vào CSV/PDF.
/// </summary>
public class CampaignResultsUnscoredFlaggedR7Tests
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

    private static void SeedFlag(CampaignDbContext db, Guid campaignId, Guid sessionId, Guid candidateId,
        string signalType, string? note = null)
    {
        db.SessionFlags.Add(new SessionFlag
        {
            Id = Guid.NewGuid(),
            CampaignId = campaignId,
            SessionId = sessionId,
            CandidateId = candidateId,
            SignalType = signalType,
            Note = note,
            DetectedAt = DateTime.UtcNow
        });
        db.SaveChanges();
    }

    private static void SeedMembership(CampaignDbContext db, Guid campaignId, Guid candidateId, string? fullName, string? email)
    {
        db.CampaignMemberships.Add(new CampaignMembership
        {
            Id = Guid.NewGuid(),
            CampaignId = campaignId,
            CandidateId = candidateId,
            FullName = fullName,
            Email = email,
            Status = MembershipStatus.Joined,
            JoinedAt = DateTime.UtcNow,
            CreatedAt = DateTime.UtcNow,
            UpdatedAt = DateTime.UtcNow
        });
        db.SaveChanges();
    }

    // (a) Session CHƯA Scored có cờ → lộ ở UnscoredFlagged, KHÔNG lẫn vào Results; session Scored giữ cờ ở Results.
    [Fact]
    public async Task Session_chua_scored_co_co_lo_ra_UnscoredFlagged_khong_lan_ranking()
    {
        using var tdb = new CampaignTestDb();
        var orgId = Guid.NewGuid();
        var campaign = SeedCampaign(tdb.Db, orgId);

        // Session Scored S1 — có row ranking + 1 cờ.
        var c1 = Guid.NewGuid();
        var s1 = Guid.NewGuid();
        SeedRanking(tdb.Db, campaign.Id, c1, s1, 75.00m);
        SeedFlag(tdb.Db, campaign.Id, s1, c1, "tab_switch");

        // Session bỏ ngang S2 — KHÔNG có ranking, nhưng 3 loại cờ (gồm face_mismatch).
        var c2 = Guid.NewGuid();
        var s2 = Guid.NewGuid();
        SeedFlag(tdb.Db, campaign.Id, s2, c2, "face_mismatch");
        SeedFlag(tdb.Db, campaign.Id, s2, c2, "paste");
        SeedFlag(tdb.Db, campaign.Id, s2, c2, "focus_lost");

        var res = await NewService(tdb.NewContext()).GetCampaignResultsAsync(orgId, campaign.Id, default);

        // Ranking KHÔNG đổi ngữ nghĩa: chỉ 1 ứng viên Scored, kèm cờ.
        Assert.Equal(1, res.TotalCandidates);
        var scored = Assert.Single(res.Results);
        Assert.Equal(s1, scored.SessionId);
        Assert.Equal("tab_switch", Assert.Single(scored.Flags).Type);

        // R7: session bỏ ngang lộ ra ở UnscoredFlagged — KHÔNG nằm trong Results.
        var un = Assert.Single(res.UnscoredFlagged);
        Assert.Equal(s2, un.SessionId);
        Assert.Equal(c2, un.CandidateId);
        Assert.Equal(new[] { "face_mismatch", "focus_lost", "paste" },
            un.Flags.Select(f => f.Type).OrderBy(x => x, StringComparer.Ordinal).ToArray());
        Assert.DoesNotContain(res.Results, r => r.SessionId == s2);
    }

    // (b) UnscoredFlagged mang danh tính (F5) + sort NHIỀU CỜ trước (đáng ngờ hơn lên đầu).
    [Fact]
    public async Task UnscoredFlagged_mang_danh_tinh_va_sort_nhieu_co_truoc()
    {
        using var tdb = new CampaignTestDb();
        var orgId = Guid.NewGuid();
        var campaign = SeedCampaign(tdb.Db, orgId);

        // Ít cờ (1) — phải xuống dưới.
        var cLow = Guid.NewGuid();
        var sLow = Guid.NewGuid();
        SeedFlag(tdb.Db, campaign.Id, sLow, cLow, "tab_switch");
        SeedMembership(tdb.Db, campaign.Id, cLow, "Ít Cờ", "low@x.co");

        // Nhiều cờ (3) — phải lên đầu.
        var cHigh = Guid.NewGuid();
        var sHigh = Guid.NewGuid();
        SeedFlag(tdb.Db, campaign.Id, sHigh, cHigh, "face_mismatch");
        SeedFlag(tdb.Db, campaign.Id, sHigh, cHigh, "no_face");
        SeedFlag(tdb.Db, campaign.Id, sHigh, cHigh, "multiple_faces");
        SeedMembership(tdb.Db, campaign.Id, cHigh, "Nhiều Cờ", "high@x.co");

        var res = await NewService(tdb.NewContext()).GetCampaignResultsAsync(orgId, campaign.Id, default);

        Assert.Equal(2, res.UnscoredFlagged.Count);
        Assert.Equal(sHigh, res.UnscoredFlagged[0].SessionId);   // nhiều cờ trước
        Assert.Equal("Nhiều Cờ", res.UnscoredFlagged[0].FullName);
        Assert.Equal("high@x.co", res.UnscoredFlagged[0].Email);
        Assert.Equal(sLow, res.UnscoredFlagged[1].SessionId);
        Assert.Equal("Ít Cờ", res.UnscoredFlagged[1].FullName);
    }

    // (c) CSV export nối dòng chưa-Scored: flags + result="Chưa chấm" + rank/total_score/scored_at TRỐNG.
    [Fact]
    public async Task Export_csv_gom_ca_unscored_flagged()
    {
        using var tdb = new CampaignTestDb();
        var orgId = Guid.NewGuid();
        var campaign = SeedCampaign(tdb.Db, orgId);

        var c1 = Guid.NewGuid(); var s1 = Guid.NewGuid();
        SeedRanking(tdb.Db, campaign.Id, c1, s1, 60.00m);

        var c2 = Guid.NewGuid(); var s2 = Guid.NewGuid();
        SeedFlag(tdb.Db, campaign.Id, s2, c2, "paste");
        SeedFlag(tdb.Db, campaign.Id, s2, c2, "face_mismatch");

        var export = await NewService(tdb.NewContext())
            .ExportCampaignResultsAsync(orgId, campaign.Id, "csv", default);

        var rows = ParseCsv(export.Content);
        Assert.Equal(2, rows.Count);   // 1 ranked + 1 unscored-flagged

        var unscored = Assert.Single(rows, r => r["session_id"] == s2.ToString());
        Assert.Equal("Chưa chấm", unscored["result"]);
        Assert.Equal(string.Empty, unscored["rank"]);
        Assert.Equal(string.Empty, unscored["total_score"]);
        Assert.Equal(string.Empty, unscored["scored_at"]);
        // MON1-B4: cột flags nay là "type(source):count"; cờ seed không set source ⇒ "Client".
        Assert.Contains("face_mismatch(Client):1", unscored["flags"]);
        Assert.Contains("paste(Client):1", unscored["flags"]);
    }

    // (d) PDF export cũng nối nhóm chưa-Scored (đọc chữ THẬT bằng PdfPig) — CSV/PDF không lệch (F16).
    [Fact]
    public async Task Export_pdf_gom_ca_unscored_flagged()
    {
        using var tdb = new CampaignTestDb();
        var orgId = Guid.NewGuid();
        var campaign = SeedCampaign(tdb.Db, orgId);

        var c1 = Guid.NewGuid(); var s1 = Guid.NewGuid();
        SeedRanking(tdb.Db, campaign.Id, c1, s1, 60.00m);

        var c2 = Guid.NewGuid(); var s2 = Guid.NewGuid();
        SeedFlag(tdb.Db, campaign.Id, s2, c2, "multiple_faces");

        var export = await NewService(tdb.NewContext())
            .ExportCampaignResultsAsync(orgId, campaign.Id, "pdf", default);

        Assert.Equal("application/pdf", export.ContentType);
        var text = ExtractPdfText(export.Content);
        Assert.Contains("Chưa chấm", text);
        Assert.Contains("multiple_faces(Client):1", text);   // MON1-B4: "type(source):count"
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

    private static string ExtractPdfText(byte[] bytes)
    {
        using var doc = PdfDocument.Open(bytes);
        return string.Join(" ", doc.GetPages().SelectMany(p => p.GetWords().Select(w => w.Text)));
    }
}
