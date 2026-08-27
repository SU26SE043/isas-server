using System.Text.Json;
using Isas.CampaignService.DTOs;
using Isas.CampaignService.Models;
using Isas.CampaignService.Services;
using Microsoft.Extensions.Logging;
using Moq;

using CampaignSvc = Isas.CampaignService.Services.CampaignService;

namespace Isas.CampaignService.Tests;

/// <summary>
/// AC1 — cờ chống gian lận phải nói được HAI thứ mà bản cũ nuốt mất:
///  (1) KHI NÀO (FirstAt/LastAt): `count:5` một mình không phân biệt "5 lần trong 10 giây" (một cú
///      alt-tab) với "5 lần rải đều 40 phút" (có hệ thống) — hai thứ HR xử lý khác hẳn nhau;
///  (2) SOI CÁI NÀO TRƯỚC (3 tầng): thứ tự cũ là ALPHABET, nên `camera_blocked` luôn nằm trên
///      `face_mismatch` chỉ vì 'c' &lt; 'f'.
/// KHÔNG phải điểm rủi ro 0-100, KHÔNG nhãn "nghi gian lận" — D13/CAMP-12: cờ = GỢI Ý cho HR.
/// </summary>
public class CampaignResultsFlagPriorityAc1Tests
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

    // Khác helper của R7Tests: cho phép ghim `detectedAt` — FirstAt/LastAt không kiểm được nếu mọi
    // dòng đều `DateTime.UtcNow`.
    private static void SeedFlag(CampaignDbContext db, Guid campaignId, Guid sessionId, Guid candidateId,
        string signalType, DateTime? detectedAt = null, string? note = null)
    {
        db.SessionFlags.Add(new SessionFlag
        {
            Id = Guid.NewGuid(),
            CampaignId = campaignId,
            SessionId = sessionId,
            CandidateId = candidateId,
            SignalType = signalType,
            Note = note,
            DetectedAt = detectedAt ?? DateTime.UtcNow
        });
        db.SaveChanges();
    }

    // ── (1) MỐC THỜI GIAN ────────────────────────────────────────────────────────────

    // FirstAt = min(detected_at), LastAt = max(detected_at) của NHÓM (session, signal_type).
    // Seed CỐ Ý không theo thứ tự thời gian: nếu ai đó lấy "dòng đầu/dòng cuối" thay vì min/max thì sai.
    [Fact]
    public async Task Ac1_FirstAt_la_min_LastAt_la_max_detected_at()
    {
        using var tdb = new CampaignTestDb();
        var orgId = Guid.NewGuid();
        var campaign = SeedCampaign(tdb.Db, orgId);
        var candidateId = Guid.NewGuid();
        var sessionId = Guid.NewGuid();

        var t0 = new DateTime(2026, 8, 27, 9, 0, 0, DateTimeKind.Utc);
        SeedFlag(tdb.Db, campaign.Id, sessionId, candidateId, "tab_switch", t0.AddMinutes(20));  // giữa
        SeedFlag(tdb.Db, campaign.Id, sessionId, candidateId, "tab_switch", t0);                 // sớm nhất
        SeedFlag(tdb.Db, campaign.Id, sessionId, candidateId, "tab_switch", t0.AddMinutes(40));  // muộn nhất

        var res = await NewService(tdb.NewContext()).GetCampaignResultsAsync(orgId, campaign.Id, default);

        var flag = Assert.Single(Assert.Single(res.UnscoredFlagged).Flags);
        Assert.Equal("tab_switch", flag.Type);
        Assert.Equal(3, flag.Count);
        Assert.Equal(t0, flag.FirstAt);
        Assert.Equal(t0.AddMinutes(40), flag.LastAt);
    }

    // Mỗi (session, signal_type) có mốc RIÊNG — không dùng chung min/max của cả buổi.
    [Fact]
    public async Task Ac1_Moc_thoi_gian_tinh_rieng_tung_loai_co()
    {
        using var tdb = new CampaignTestDb();
        var orgId = Guid.NewGuid();
        var campaign = SeedCampaign(tdb.Db, orgId);
        var candidateId = Guid.NewGuid();
        var sessionId = Guid.NewGuid();

        var t0 = new DateTime(2026, 8, 27, 9, 0, 0, DateTimeKind.Utc);
        SeedFlag(tdb.Db, campaign.Id, sessionId, candidateId, "paste", t0);
        SeedFlag(tdb.Db, campaign.Id, sessionId, candidateId, "paste", t0.AddMinutes(2));
        SeedFlag(tdb.Db, campaign.Id, sessionId, candidateId, "face_mismatch", t0.AddMinutes(30));

        var res = await NewService(tdb.NewContext()).GetCampaignResultsAsync(orgId, campaign.Id, default);
        var flags = Assert.Single(res.UnscoredFlagged).Flags;

        var paste = Assert.Single(flags, f => f.Type == "paste");
        Assert.Equal(t0, paste.FirstAt);
        Assert.Equal(t0.AddMinutes(2), paste.LastAt);

        var mismatch = Assert.Single(flags, f => f.Type == "face_mismatch");
        Assert.Equal(t0.AddMinutes(30), mismatch.FirstAt);
        Assert.Equal(t0.AddMinutes(30), mismatch.LastAt);   // 1 lần → first == last
    }

    // Hợp đồng dây với FE: hai field mới ra JSON camelCase `firstAt`/`lastAt`.
    // ⚠ Sentinel ASCII CÓ CHỦ ĐÍCH: System.Text.Json escape non-ASCII (\uXXXX) nên assert chuỗi tiếng
    // Việt vào JSON cho kết quả XANH GIẢ. Khoá cả thứ tự khai (additive Ở CUỐI): client cũ đọc tuần tự
    // vẫn thấy type/count/note ở đúng chỗ.
    [Fact]
    public void Ac1_FlagDto_serialize_ra_firstAt_lastAt_camelCase_o_cuoi()
    {
        var dto = new FlagDto
        {
            Type = "SENTINEL_TYPE",
            Count = 2,
            Note = "SENTINEL_NOTE",
            FirstAt = new DateTime(2026, 8, 27, 9, 0, 0, DateTimeKind.Utc),
            LastAt = new DateTime(2026, 8, 27, 9, 40, 0, DateTimeKind.Utc)
        };

        // JsonSerializerDefaults.Web = đúng chính sách đặt tên ASP.NET dùng (Program.cs không override).
        var json = JsonSerializer.Serialize(dto, new JsonSerializerOptions(JsonSerializerDefaults.Web));

        Assert.Contains("\"firstAt\"", json);
        Assert.Contains("\"lastAt\"", json);
        Assert.DoesNotContain("\"FirstAt\"", json);
        Assert.DoesNotContain("\"LastAt\"", json);

        // Additive Ở CUỐI: 3 khoá cũ phải đứng trước 2 khoá mới.
        Assert.True(json.IndexOf("\"type\"", StringComparison.Ordinal) < json.IndexOf("\"firstAt\"", StringComparison.Ordinal));
        Assert.True(json.IndexOf("\"count\"", StringComparison.Ordinal) < json.IndexOf("\"firstAt\"", StringComparison.Ordinal));
        Assert.True(json.IndexOf("\"note\"", StringComparison.Ordinal) < json.IndexOf("\"firstAt\"", StringComparison.Ordinal));
        Assert.True(json.IndexOf("\"firstAt\"", StringComparison.Ordinal) < json.IndexOf("\"lastAt\"", StringComparison.Ordinal));
    }

    // ── (2) 3 TẦNG ƯU TIÊN ĐỌC ───────────────────────────────────────────────────────

    // 🔴 Buổi có 1 cờ DANH TÍNH phải xếp TRÊN buổi có 5 cờ HÀNH VI. Sắp thuần theo tổng count (hành vi
    // cũ) chôn nhóm nghi-sai-người xuống dưới nhiễu — HR đọc từ trên xuống nên đó là đường bỏ sót thật.
    [Fact]
    public async Task Ac1_Mot_face_mismatch_xep_tren_nam_paste()
    {
        using var tdb = new CampaignTestDb();
        var orgId = Guid.NewGuid();
        var campaign = SeedCampaign(tdb.Db, orgId);

        var sNoisy = Guid.NewGuid();
        var cNoisy = Guid.NewGuid();
        for (int i = 0; i < 5; i++)
            SeedFlag(tdb.Db, campaign.Id, sNoisy, cNoisy, "paste");

        var sIdentity = Guid.NewGuid();
        var cIdentity = Guid.NewGuid();
        SeedFlag(tdb.Db, campaign.Id, sIdentity, cIdentity, "face_mismatch");

        var res = await NewService(tdb.NewContext()).GetCampaignResultsAsync(orgId, campaign.Id, default);

        Assert.Equal(2, res.UnscoredFlagged.Count);
        Assert.Equal(sIdentity, res.UnscoredFlagged[0].SessionId);   // 1 cờ danh tính > 5 cờ hành vi
        Assert.Equal(sNoisy, res.UnscoredFlagged[1].SessionId);
    }

    // Tổng count vẫn là tie-break TRONG CÙNG một tầng (không bị tầng nuốt mất).
    [Fact]
    public async Task Ac1_Cung_tang_thi_nhieu_co_hon_len_truoc()
    {
        using var tdb = new CampaignTestDb();
        var orgId = Guid.NewGuid();
        var campaign = SeedCampaign(tdb.Db, orgId);

        var sFew = Guid.NewGuid();
        SeedFlag(tdb.Db, campaign.Id, sFew, Guid.NewGuid(), "tab_switch");

        var sMany = Guid.NewGuid();
        var cMany = Guid.NewGuid();
        SeedFlag(tdb.Db, campaign.Id, sMany, cMany, "tab_switch");
        SeedFlag(tdb.Db, campaign.Id, sMany, cMany, "paste");
        SeedFlag(tdb.Db, campaign.Id, sMany, cMany, "focus_lost");

        var res = await NewService(tdb.NewContext()).GetCampaignResultsAsync(orgId, campaign.Id, default);

        Assert.Equal(sMany, res.UnscoredFlagged[0].SessionId);
        Assert.Equal(sFew, res.UnscoredFlagged[1].SessionId);
    }

    // 🔴 `identity_unverified` thuộc tầng MÔI TRƯỜNG, KHÔNG phải danh tính. Nó nghĩa đen là "KHÔNG
    // KẾT LUẬN ĐƯỢC danh tính" (sinh từ bản vá ảnh mốc đen 08/08 — thường là ảnh tham chiếu hỏng, tức
    // lỗi HỆ THỐNG). Xếp cạnh `multiple_faces` là đảo ngược mục đích của chính nó: biến "chưa đo được"
    // thành "đã bắt được" rồi đẩy lên đầu danh sách HR đúng nhóm ta không có bằng chứng gì.
    [Fact]
    public async Task Ac1_Identity_unverified_thuoc_tang_moi_truong_khong_phai_danh_tinh()
    {
        using var tdb = new CampaignTestDb();
        var orgId = Guid.NewGuid();
        var campaign = SeedCampaign(tdb.Db, orgId);

        var sUnverified = Guid.NewGuid();
        SeedFlag(tdb.Db, campaign.Id, sUnverified, Guid.NewGuid(), "identity_unverified");

        var sBehavior = Guid.NewGuid();
        SeedFlag(tdb.Db, campaign.Id, sBehavior, Guid.NewGuid(), "tab_switch");

        var res = await NewService(tdb.NewContext()).GetCampaignResultsAsync(orgId, campaign.Id, default);

        // Cùng 1 cờ mỗi buổi → chỉ TẦNG phân định. Hành vi (2) > môi trường (1).
        Assert.Equal(sBehavior, res.UnscoredFlagged[0].SessionId);
        Assert.Equal(sUnverified, res.UnscoredFlagged[1].SessionId);
    }

    // Loại lạ (FE/AIService deploy trước Campaign, hoặc dữ liệu cũ) → tầng THẤP NHẤT, KHÔNG ném.
    // Chiều mặc định có chủ đích: cờ chưa ai phân loại không được tự leo lên đầu danh sách HR.
    [Fact]
    public async Task Ac1_Loai_co_la_roi_ve_tang_thap_nhat_khong_nem()
    {
        using var tdb = new CampaignTestDb();
        var orgId = Guid.NewGuid();
        var campaign = SeedCampaign(tdb.Db, orgId);

        var sUnknown = Guid.NewGuid();
        SeedFlag(tdb.Db, campaign.Id, sUnknown, Guid.NewGuid(), "co_tuong_lai_chua_phan_loai");

        var sBehavior = Guid.NewGuid();
        SeedFlag(tdb.Db, campaign.Id, sBehavior, Guid.NewGuid(), "focus_lost");

        var res = await NewService(tdb.NewContext()).GetCampaignResultsAsync(orgId, campaign.Id, default);

        Assert.Equal(2, res.UnscoredFlagged.Count);                  // không ném, vẫn trả đủ
        Assert.Equal(sBehavior, res.UnscoredFlagged[0].SessionId);   // loại lạ xuống dưới
        Assert.Equal(sUnknown, res.UnscoredFlagged[1].SessionId);
    }

    // 🔴 Flags[] TRONG một buổi hết xếp theo alphabet: `face_mismatch` phải đứng trước `camera_blocked`
    // dù 'c' < 'f'. Đây là thứ tự HR đọc trong ô cờ của bảng/CSV/PDF.
    [Fact]
    public async Task Ac1_Flags_trong_row_xep_theo_tang_khong_theo_alphabet()
    {
        using var tdb = new CampaignTestDb();
        var orgId = Guid.NewGuid();
        var campaign = SeedCampaign(tdb.Db, orgId);
        var sessionId = Guid.NewGuid();
        var candidateId = Guid.NewGuid();

        // Seed theo đúng thứ tự alphabet để bản cũ "trông như đúng" nếu không đổi gì.
        SeedFlag(tdb.Db, campaign.Id, sessionId, candidateId, "camera_blocked");   // môi trường (1)
        SeedFlag(tdb.Db, campaign.Id, sessionId, candidateId, "face_mismatch");    // danh tính  (3)
        SeedFlag(tdb.Db, campaign.Id, sessionId, candidateId, "tab_switch");       // hành vi    (2)

        var res = await NewService(tdb.NewContext()).GetCampaignResultsAsync(orgId, campaign.Id, default);
        var types = Assert.Single(res.UnscoredFlagged).Flags.Select(f => f.Type).ToArray();

        Assert.Equal(new[] { "face_mismatch", "tab_switch", "camera_blocked" }, types);
    }

    // Trong CÙNG tầng, nhiều lần hơn đứng trước; tên chỉ còn là tie-break CUỐI (thứ tự ổn định).
    [Fact]
    public async Task Ac1_Flags_trong_row_cung_tang_thi_count_desc_roi_moi_den_ten()
    {
        using var tdb = new CampaignTestDb();
        var orgId = Guid.NewGuid();
        var campaign = SeedCampaign(tdb.Db, orgId);
        var sessionId = Guid.NewGuid();
        var candidateId = Guid.NewGuid();

        SeedFlag(tdb.Db, campaign.Id, sessionId, candidateId, "focus_lost");       // 1 lần
        for (int i = 0; i < 3; i++)
            SeedFlag(tdb.Db, campaign.Id, sessionId, candidateId, "tab_switch");   // 3 lần
        SeedFlag(tdb.Db, campaign.Id, sessionId, candidateId, "paste");            // 1 lần

        var res = await NewService(tdb.NewContext()).GetCampaignResultsAsync(orgId, campaign.Id, default);
        var types = Assert.Single(res.UnscoredFlagged).Flags.Select(f => f.Type).ToArray();

        // tab_switch (3) trước; focus_lost/paste cùng count → Ordinal ("focus_lost" < "paste").
        Assert.Equal(new[] { "tab_switch", "focus_lost", "paste" }, types);
    }

    // Cùng cách xếp áp cho `CampaignResultRow.Flags` (ứng viên ĐÃ Scored) — cùng một GroupFlagsBySession,
    // nhưng đây là bảng HR nhìn nhiều nhất nên khoá luôn để tách bạch hai đường đọc không trôi khỏi nhau.
    [Fact]
    public async Task Ac1_Flags_cua_ung_vien_da_scored_cung_xep_theo_tang()
    {
        using var tdb = new CampaignTestDb();
        var orgId = Guid.NewGuid();
        var campaign = SeedCampaign(tdb.Db, orgId);
        var candidateId = Guid.NewGuid();
        var sessionId = Guid.NewGuid();

        tdb.Db.CampaignRankings.Add(new CampaignRanking
        {
            Id = Guid.NewGuid(),
            CampaignId = campaign.Id,
            CandidateId = candidateId,
            SessionId = sessionId,
            TotalScore = 70m,
            UpdatedAt = DateTime.UtcNow
        });
        tdb.Db.SaveChanges();

        var t0 = new DateTime(2026, 8, 27, 9, 0, 0, DateTimeKind.Utc);
        SeedFlag(tdb.Db, campaign.Id, sessionId, candidateId, "camera_blocked", t0);
        SeedFlag(tdb.Db, campaign.Id, sessionId, candidateId, "multi_voice", t0.AddMinutes(10));

        var res = await NewService(tdb.NewContext()).GetCampaignResultsAsync(orgId, campaign.Id, default);
        var row = Assert.Single(res.Results);

        Assert.Equal(new[] { "multi_voice", "camera_blocked" }, row.Flags.Select(f => f.Type).ToArray());
        Assert.Equal(t0.AddMinutes(10), row.Flags[0].FirstAt);   // mốc đi kèm cả đường Scored
    }
}
