using System.Text;
using System.Text.Json;
using Isas.CampaignService.DTOs;
using Isas.CampaignService.Models;
using Isas.CampaignService.Services;
using Isas.Shared.Scoring;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging;
using Moq;

using CampaignSvc = Isas.CampaignService.Services.CampaignService;

namespace Isas.CampaignService.Tests;

/// <summary>
/// SCP1-B10 — nhãn chính sách (policy_version/policy_name) + cờ lùi (score_fallback) phải tới
/// campaign_rankings → bảng kết quả → CSV NGAY trên đường chấm THƯỜNG, không chỉ sau khi HR bấm "áp"
/// (B8). Bug 30/08: Interview phát ScoreFallback + CampaignPolicyVersion trên event nhưng
/// SessionScoredMessage KHÔNG khai property ⇒ System.Text.Json bỏ khoá lạ ⇒ mất, không lỗi không log;
/// và RankingEventHandler không ghi 3 cột ở cả hai nhánh (tạo mới / upsert).
///
/// Test đi TỚI nơi hợp đồng hứa (GetCampaignResultsAsync + ExportCampaignResultsAsync) — KHÔNG dừng
/// ở tầng DTO: bug này sống được chính vì hai bên đều có test hợp đồng riêng và đều xanh.
/// </summary>
public class ScoringPolicyRankingLabelB10Tests
{
    private static CampaignSvc NewService(CampaignDbContext db) =>
        new(db, Mock.Of<IFileService>(), Mock.Of<ILogger<CampaignSvc>>(),
            Mock.Of<IParserService>(), Mock.Of<ICriteriaSuggester>(),
            Mock.Of<IInvitationEmailPublisher>());

    private static RankingEventHandler NewHandler(CampaignDbContext db) =>
        new(db, Mock.Of<ILogger<RankingEventHandler>>());

    private static ScoringPolicy SeedPolicy(Guid campaignId, int version, string name) => new()
    {
        Id = Guid.NewGuid(), CampaignId = campaignId, Kind = ScoringExpressionKind.Interview,
        Version = version, EngineVersion = "1", Name = name, Expression = "weighted_avg_pct",
        CreatedAt = DateTime.UtcNow
    };

    private static SessionScoredMessage Evt(Guid campaignId, int? policyVersion, bool fallback = false) => new()
    {
        SessionId = Guid.NewGuid(), CampaignId = campaignId, CandidateId = Guid.NewGuid(),
        TotalScore = 68m, ScoredAt = DateTime.UtcNow,
        CampaignPolicyVersion = policyVersion, ScoreFallback = fallback
    };

    // (a) buổi ghim v2 → chấm THƯỜNG → bảng kết quả có policy_version=2 + policy_name đúng.
    [Fact]
    public async Task Buoi_ghim_v2_cham_thuong_thi_results_co_policy_version_va_name()
    {
        using var tdb = new CampaignTestDb();
        var owner = Guid.NewGuid();
        var camp = CampaignTestDb.NewCampaign(owner, CampaignStatus.Active);
        tdb.Db.Campaigns.Add(camp);
        tdb.Db.ScoringPolicies.AddRange(
            SeedPolicy(camp.Id, 1, "Ban nhap v1"),
            SeedPolicy(camp.Id, 2, "Khong bu tru v2"),
            SeedPolicy(camp.Id, 3, "Con tro da di toi v3"));   // con trỏ campaign KHÁC version buổi
        await tdb.Db.SaveChangesAsync();

        var evt = Evt(camp.Id, policyVersion: 2);
        await NewHandler(tdb.NewContext()).HandleSessionScoredAsync(evt, default);

        var res = await NewService(tdb.NewContext()).GetCampaignResultsAsync(owner, camp.Id, default);
        var row = res.Results.Single();
        Assert.Equal(2, row.PolicyVersion);
        Assert.Equal("Khong bu tru v2", row.PolicyName);   // tên theo version CỦA SỰ KIỆN, không theo con trỏ
        Assert.False(row.ScoreFallback);
    }

    // (b) scoreFallback=true trên event → GET /results trả true → CSV cột score_fallback in True.
    [Fact]
    public async Task ScoreFallback_true_len_toi_results_va_CSV()
    {
        using var tdb = new CampaignTestDb();
        var owner = Guid.NewGuid();
        var camp = CampaignTestDb.NewCampaign(owner, CampaignStatus.Active);
        tdb.Db.Campaigns.Add(camp);
        tdb.Db.ScoringPolicies.Add(SeedPolicy(camp.Id, 1, "Chinh sach v1"));
        await tdb.Db.SaveChangesAsync();

        await NewHandler(tdb.NewContext()).HandleSessionScoredAsync(
            Evt(camp.Id, policyVersion: 1, fallback: true), default);

        var res = await NewService(tdb.NewContext()).GetCampaignResultsAsync(owner, camp.Id, default);
        Assert.True(res.Results.Single().ScoreFallback);

        var csv = Encoding.UTF8.GetString(
            (await NewService(tdb.NewContext()).ExportCampaignResultsAsync(owner, camp.Id, "csv", default)).Content);
        var line = csv.Split('\n', StringSplitOptions.RemoveEmptyEntries)[1].TrimEnd('\r');
        // đuôi: ...,rubric_version,policy_version,policy_name,score_fallback (rubric_version rỗng ở đây)
        Assert.EndsWith(",,1,Chinh sach v1,True", line);
    }

    // (c) buổi KHÔNG ghim chính sách (campaign chưa áp) → cả ba vẫn null/false, KHÔNG hồi quy.
    [Fact]
    public async Task Buoi_khong_ghim_chinh_sach_thi_ba_cot_null_false()
    {
        using var tdb = new CampaignTestDb();
        var owner = Guid.NewGuid();
        var camp = CampaignTestDb.NewCampaign(owner, CampaignStatus.Active);
        tdb.Db.Campaigns.Add(camp);
        await tdb.Db.SaveChangesAsync();

        await NewHandler(tdb.NewContext()).HandleSessionScoredAsync(
            Evt(camp.Id, policyVersion: null), default);

        using var check = tdb.NewContext();
        var row = await check.CampaignRankings.SingleAsync();
        Assert.Null(row.PolicyVersion);
        Assert.Null(row.PolicyName);
        Assert.False(row.ScoreFallback);
    }

    // Hàng ranking ĐÃ tồn tại (dòng trước B10 / B8 đã xoá nhãn) → SessionScored tới → nhánh UPSERT
    // PHẢI điền 3 cột policy (outbox at-least-once: bàn giao lại đi vào đúng nhánh này, CẤM bỏ nhánh).
    [Fact]
    public async Task Upsert_hang_da_ton_tai_thi_van_ghi_3_cot()
    {
        using var tdb = new CampaignTestDb();
        var owner = Guid.NewGuid();
        var camp = CampaignTestDb.NewCampaign(owner, CampaignStatus.Active);
        var sessionId = Guid.NewGuid();
        tdb.Db.Campaigns.Add(camp);
        tdb.Db.ScoringPolicies.Add(SeedPolicy(camp.Id, 3, "Khong bu tru v3"));
        tdb.Db.CampaignRankings.Add(new CampaignRanking
        {
            Id = Guid.NewGuid(), CampaignId = camp.Id, CandidateId = Guid.NewGuid(),
            SessionId = sessionId, TotalScore = 50m, UpdatedAt = DateTime.UtcNow
            // KHÔNG có policy fields — dòng trước B10 / B8 đã xoá nhãn
        });
        await tdb.Db.SaveChangesAsync();

        var evt = Evt(camp.Id, policyVersion: 3, fallback: true);
        evt.SessionId = sessionId;   // trùng session_id đã có → đi nhánh UPSERT
        await NewHandler(tdb.NewContext()).HandleSessionScoredAsync(evt, default);

        using var check = tdb.NewContext();
        var row = await check.CampaignRankings.SingleAsync(r => r.SessionId == sessionId);
        Assert.Equal(3, row.PolicyVersion);
        Assert.Equal("Khong bu tru v3", row.PolicyName);
        Assert.True(row.ScoreFallback);
    }

    // Bug 30/08 sống ở BIÊN DESERIALIZE: Interview PHÁT scoreFallback + campaignPolicyVersion trên
    // JSON, SessionScoredMessage phải khai property BÌNH-DANH để STJ bind. Test đi qua Deserialize
    // (không dựng object trực tiếp) — bỏ khai / đổi [JsonPropertyName] là dòng này ĐỎ.
    [Fact]
    public async Task Event_moi_co_scoreFallback_va_policyVersion_qua_JSON_thi_bind_dung()
    {
        using var tdb = new CampaignTestDb();
        var owner = Guid.NewGuid();
        var camp = CampaignTestDb.NewCampaign(owner, CampaignStatus.Active);
        tdb.Db.Campaigns.Add(camp);
        tdb.Db.ScoringPolicies.Add(SeedPolicy(camp.Id, 2, "Khong bu tru v2"));
        await tdb.Db.SaveChangesAsync();

        // Đúng shape Interview phát ra (SessionScoredEvent) — camelCase, có scoreFallback + campaignPolicyVersion.
        var json = JsonSerializer.Serialize(new
        {
            sessionId = Guid.NewGuid(), campaignId = camp.Id, candidateId = Guid.NewGuid(),
            totalScore = 68m, scoredAt = DateTime.UtcNow,
            scoreFallback = true, campaignPolicyVersion = 2
        });
        var evt = JsonSerializer.Deserialize<SessionScoredMessage>(
            json, new JsonSerializerOptions { PropertyNameCaseInsensitive = true })!;

        await NewHandler(tdb.NewContext()).HandleSessionScoredAsync(evt, default);

        var res = await NewService(tdb.NewContext()).GetCampaignResultsAsync(owner, camp.Id, default);
        var row = res.Results.Single();
        Assert.True(row.ScoreFallback);              // khoá scoreFallback bind được
        Assert.Equal(2, row.PolicyVersion);          // khoá campaignPolicyVersion bind được
        Assert.Equal("Khong bu tru v2", row.PolicyName);
    }

    // Bản Interview CŨ không gửi khoá scoreFallback (deploy không nguyên tử) ⇒ deserialize ra false,
    // KHÔNG vỡ. Tái tạo đúng bug 30/08 nếu property bị bỏ khai lại.
    [Fact]
    public async Task Event_cu_khong_co_scoreFallback_thi_false_khong_vo()
    {
        using var tdb = new CampaignTestDb();
        var camp = CampaignTestDb.NewCampaign(Guid.NewGuid(), CampaignStatus.Active);
        tdb.Db.Campaigns.Add(camp);
        await tdb.Db.SaveChangesAsync();

        var json = JsonSerializer.Serialize(new
        {
            sessionId = Guid.NewGuid(), campaignId = camp.Id, candidateId = Guid.NewGuid(),
            totalScore = 70m, scoredAt = DateTime.UtcNow
        });
        var evt = JsonSerializer.Deserialize<SessionScoredMessage>(
            json, new JsonSerializerOptions { PropertyNameCaseInsensitive = true })!;

        await NewHandler(tdb.NewContext()).HandleSessionScoredAsync(evt, default);

        using var check = tdb.NewContext();
        var row = await check.CampaignRankings.FirstAsync(r => r.SessionId == evt.SessionId);
        Assert.False(row.ScoreFallback);
        Assert.Null(row.PolicyVersion);
    }
}
