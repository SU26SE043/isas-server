using Isas.CampaignService.Models;
using Isas.CampaignService.Services;
using Microsoft.Extensions.Logging;
using Moq;

// 'CampaignService' vừa là namespace vừa là tên class → alias cho rõ ràng (mẫu SessionFlagTests).
using CampaignSvc = Isas.CampaignService.Services.CampaignService;

namespace Isas.CampaignService.Tests;

/// <summary>
/// Log cờ chống gian lận THEO GIÂY (`GetSessionFlagTimelineAsync`) — khác `Flags[]` gộp count trong
/// `GetCampaignResultsAsync` (SEC-4): đây là đường đọc TRỰC TIẾP session_flags, giữ nguyên `DetectedAt`
/// và thứ tự thời gian, dùng cho drill-down của HR (mẫu AI4 transcript, nhưng KHÔNG đòi ranking row).
/// </summary>
public class SessionFlagTimelineTests
{
    private static CampaignSvc NewService(CampaignTestDb tdb) =>
        new(tdb.NewContext(), Mock.Of<IFileService>(), Mock.Of<ILogger<CampaignSvc>>(),
            Mock.Of<IParserService>(), Mock.Of<ICriteriaSuggester>(), Mock.Of<IInvitationEmailPublisher>());

    private static void AddFlag(
        CampaignTestDb tdb, Guid campaignId, Guid sessionId, Guid candidateId,
        string type, DateTime detectedAt, string? note = null)
    {
        tdb.Db.SessionFlags.Add(new SessionFlag
        {
            Id = Guid.NewGuid(),
            SessionId = sessionId,
            CampaignId = campaignId,
            CandidateId = candidateId,
            SignalType = type,
            Note = note,
            DetectedAt = detectedAt
        });
        tdb.Db.SaveChanges();
    }

    // ── Ngoài org → 404 (KeyNotFoundException, khớp GetCampaignResultsAsync/GetSessionTranscriptAsync) ──
    [Fact]
    public async Task NgoaiOrg_KeyNotFoundException()
    {
        using var tdb = new CampaignTestDb();
        var owner = Guid.NewGuid();
        var outsider = Guid.NewGuid();
        var campaign = CampaignTestDb.NewCampaign(owner, CampaignStatus.Active);
        tdb.Db.Campaigns.Add(campaign);
        tdb.Db.SaveChanges();

        await Assert.ThrowsAsync<KeyNotFoundException>(() =>
            NewService(tdb).GetSessionFlagTimelineAsync(outsider, campaign.Id, Guid.NewGuid(), default));
    }

    // ── Nhiều loại cờ, nhiều lần → trả TỪNG SỰ KIỆN theo DetectedAt tăng dần (KHÔNG gộp count) ──
    [Fact]
    public async Task NhieuCo_TraDungThuTuThoiGian_KhongGomCount()
    {
        using var tdb = new CampaignTestDb();
        var orgId = Guid.NewGuid();
        var candidateId = Guid.NewGuid();
        var sessionId = Guid.NewGuid();
        var campaign = CampaignTestDb.NewCampaign(orgId, CampaignStatus.Active);
        tdb.Db.Campaigns.Add(campaign);
        tdb.Db.SaveChanges();

        var t0 = new DateTime(2026, 8, 16, 9, 0, 0, DateTimeKind.Utc);
        AddFlag(tdb, campaign.Id, sessionId, candidateId, "tab_switch", t0.AddSeconds(30));
        AddFlag(tdb, campaign.Id, sessionId, candidateId, "no_face", t0, "không thấy mặt");
        AddFlag(tdb, campaign.Id, sessionId, candidateId, "tab_switch", t0.AddSeconds(75));

        var res = await NewService(tdb).GetSessionFlagTimelineAsync(orgId, campaign.Id, sessionId, default);

        Assert.Equal(sessionId, res.SessionId);
        Assert.Equal(candidateId, res.CandidateId);
        Assert.Equal(3, res.Events.Count);
        // Không gộp: 2 dòng tab_switch riêng biệt, giữ nguyên mốc giây gốc, sắp tăng dần.
        Assert.Equal(new[] { "no_face", "tab_switch", "tab_switch" },
            res.Events.Select(e => e.SignalType).ToArray());
        Assert.Equal(t0, res.Events[0].DetectedAt);
        Assert.Equal("không thấy mặt", res.Events[0].Note);
        Assert.Equal(t0.AddSeconds(30), res.Events[1].DetectedAt);
        Assert.Equal(t0.AddSeconds(75), res.Events[2].DetectedAt);
    }

    // ── Session CHƯA Scored (không có ranking row) vẫn xem được — khác GetSessionTranscriptAsync (AI4) ──
    // Đây đúng nhóm R7 (đáng ngờ nhất: bỏ ngang giữa buổi); log theo giây không được phép biến mất theo.
    [Fact]
    public async Task SessionChuaScored_KhongCoRankingRow_VanTraDuocLog()
    {
        using var tdb = new CampaignTestDb();
        var orgId = Guid.NewGuid();
        var sessionId = Guid.NewGuid();
        var campaign = CampaignTestDb.NewCampaign(orgId, CampaignStatus.Active);
        tdb.Db.Campaigns.Add(campaign);
        tdb.Db.SaveChanges();
        // KHÔNG thêm CampaignRanking — session bỏ ngang, chưa từng Scored.
        AddFlag(tdb, campaign.Id, sessionId, Guid.NewGuid(), "focus_lost", DateTime.UtcNow);

        var res = await NewService(tdb).GetSessionFlagTimelineAsync(orgId, campaign.Id, sessionId, default);

        Assert.Single(res.Events);
        Assert.Equal("focus_lost", res.Events[0].SignalType);
    }

    // ── Session không có cờ nào → Events rỗng, KHÔNG 404 (404 chỉ dành cho sai org) ──
    [Fact]
    public async Task SessionKhongCoCo_EventsRong_KhongPhai404()
    {
        using var tdb = new CampaignTestDb();
        var orgId = Guid.NewGuid();
        var campaign = CampaignTestDb.NewCampaign(orgId, CampaignStatus.Active);
        tdb.Db.Campaigns.Add(campaign);
        tdb.Db.SaveChanges();

        var res = await NewService(tdb).GetSessionFlagTimelineAsync(orgId, campaign.Id, Guid.NewGuid(), default);

        Assert.Empty(res.Events);
        Assert.Equal(Guid.Empty, res.CandidateId);
    }
}
