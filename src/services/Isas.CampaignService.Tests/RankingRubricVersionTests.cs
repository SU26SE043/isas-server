using System.Text;
using System.Text.Json;
using Isas.CampaignService.DTOs;
using Isas.CampaignService.Models;
using Isas.CampaignService.Services;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging;
using Moq;

using CampaignSvc = Isas.CampaignService.Services.CampaignService;

namespace Isas.CampaignService.Tests;

/// <summary>
/// CAMP-18 — nhãn thước đo đi kèm điểm ở bảng xếp hạng và bản xuất CSV.
///
/// <para>Tiền lệ ép làm việc này: <c>scoring_scope_version</c> sinh ra vì "thu hẹp phạm vi chấm làm
/// điểm không so sánh được, mà CAMP-10 · BC15 · F14 đang đem so thẳng". ĐỔI MỐC là đổi thước mạnh
/// hơn đổi phạm vi.</para>
///
/// <para>⚠ <c>null</c> = KHÔNG BIẾT, không phải v1 (BK23: suy "biết" từ "không biết" là bịa).</para>
/// </summary>
public class RankingRubricVersionTests
{
    private static CampaignSvc NewService(CampaignDbContext db) =>
        new(db, Mock.Of<IFileService>(), Mock.Of<ILogger<CampaignSvc>>(),
            Mock.Of<IParserService>(), Mock.Of<ICriteriaSuggester>(),
            Mock.Of<IInvitationEmailPublisher>());

    private static RankingEventHandler NewHandler(CampaignDbContext db) =>
        new(db, Mock.Of<ILogger<RankingEventHandler>>());

    private static CampaignRanking SeedRanking(Guid campaignId, decimal score, int? rubricVersion)
        => new()
        {
            Id = Guid.NewGuid(), CampaignId = campaignId, CandidateId = Guid.NewGuid(),
            SessionId = Guid.NewGuid(), TotalScore = score, RubricVersion = rubricVersion,
            UpdatedAt = DateTime.UtcNow
        };

    // ── Consumer ─────────────────────────────────────────────────────────

    [Fact]
    public async Task Event_mang_rubricVersion_thi_luu_lai()
    {
        using var tdb = new CampaignTestDb();
        var camp = CampaignTestDb.NewCampaign(Guid.NewGuid(), CampaignStatus.Active);
        tdb.Db.Campaigns.Add(camp);
        await tdb.Db.SaveChangesAsync();

        var sessionId = Guid.NewGuid();
        await NewHandler(tdb.NewContext()).HandleSessionScoredAsync(new SessionScoredMessage
        {
            SessionId = sessionId, CampaignId = camp.Id, CandidateId = Guid.NewGuid(),
            TotalScore = 80m, ScoredAt = DateTime.UtcNow, RubricVersion = 3
        }, default);

        using var check = tdb.NewContext();
        Assert.Equal(3, (await check.CampaignRankings.FirstAsync(r => r.SessionId == sessionId)).RubricVersion);
    }

    // Bản Interview CŨ không gửi field này (hai service deploy không nguyên tử) ⇒ phải nhận NULL và
    // KHÔNG vỡ. Đây là ca sẽ xảy ra thật trong cửa sổ giữa hai lần khởi động.
    [Fact]
    public async Task Event_KHONG_co_field_thi_null_va_khong_vo()
    {
        using var tdb = new CampaignTestDb();
        var camp = CampaignTestDb.NewCampaign(Guid.NewGuid(), CampaignStatus.Active);
        tdb.Db.Campaigns.Add(camp);
        await tdb.Db.SaveChangesAsync();

        // Payload đúng như bản cũ phát ra: không hề có khoá rubricVersion.
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
        Assert.Null(row.RubricVersion);   // "không biết", KHÔNG phải 1
        Assert.Equal(70m, row.TotalScore);
    }

    // ── Bảng kết quả ─────────────────────────────────────────────────────

    [Fact]
    public async Task Results_tra_rubricVersion_tung_dong_va_ban_dang_hieu_luc()
    {
        using var tdb = new CampaignTestDb();
        var owner = Guid.NewGuid();
        var camp = CampaignTestDb.NewCampaign(owner, CampaignStatus.Active);
        camp.RubricVersion = 2;
        tdb.Db.Campaigns.Add(camp);
        tdb.Db.CampaignRankings.AddRange(
            SeedRanking(camp.Id, 90m, rubricVersion: 2),
            SeedRanking(camp.Id, 80m, rubricVersion: 1),
            SeedRanking(camp.Id, 70m, rubricVersion: null));
        await tdb.Db.SaveChangesAsync();

        var res = await NewService(tdb.NewContext()).GetCampaignResultsAsync(owner, camp.Id, default);

        Assert.Equal(2, res.CurrentRubricVersion);
        Assert.Equal(new int?[] { 2, 1, null }, res.Results.Select(r => r.RubricVersion));
    }

    // ── Export CSV ───────────────────────────────────────────────────────

    // BẮT BUỘC có cột này: thiếu nó thì HR xuất Excel rồi trộn điểm của hai thước đo với nhau, hoàn
    // toàn ngoài tầm mọi cảnh báo mà app hiện trên màn hình.
    [Fact]
    public async Task CSV_co_cot_rubric_version()
    {
        using var tdb = new CampaignTestDb();
        var owner = Guid.NewGuid();
        var camp = CampaignTestDb.NewCampaign(owner, CampaignStatus.Active);
        camp.RubricVersion = 2;
        tdb.Db.Campaigns.Add(camp);
        tdb.Db.CampaignRankings.AddRange(
            SeedRanking(camp.Id, 90m, rubricVersion: 2),
            SeedRanking(camp.Id, 80m, rubricVersion: null));
        await tdb.Db.SaveChangesAsync();

        var csv = Encoding.UTF8.GetString(
            (await NewService(tdb.NewContext()).ExportCampaignResultsAsync(owner, camp.Id, "csv", default)).Content);
        var lines = csv.Split('\n', StringSplitOptions.RemoveEmptyEntries);

        // SCP1/HĐ-5 thêm policy_version,policy_name,score_fallback ở SAU rubric_version (vẫn ở ĐUÔI).
        // RNK1/HĐ-3 nối tiếp 9 cột số câu + CV + điểm sàn ở ĐUÔI (Index 13..21).
        Assert.Contains(",rubric_version,policy_version,policy_name,score_fallback,answered,", lines[0].TrimEnd('\r'));
        Assert.EndsWith(",below_cutoff", lines[0].TrimEnd('\r'));
        // rubric_version=2 · 3 ô SCP1 rỗng/rỗng/False · 9 ô RNK1 rỗng (seed ranking không có ScoringInputs/CV).
        Assert.EndsWith(",2,,,False,,,,,,,,,", lines[1].TrimEnd('\r'));
        // rubric_version null → ô RỖNG, không phải "1": bản xuất không khẳng định thứ mình không biết.
        Assert.EndsWith(",,,,False,,,,,,,,,", lines[2].TrimEnd('\r'));
    }

    // Thứ tự cột cũ phải giữ nguyên — HR/script đang đọc theo chỉ số.
    [Fact]
    public async Task CSV_giu_nguyen_thu_tu_cot_cu()
    {
        using var tdb = new CampaignTestDb();
        var owner = Guid.NewGuid();
        var camp = CampaignTestDb.NewCampaign(owner, CampaignStatus.Active);
        tdb.Db.Campaigns.Add(camp);
        await tdb.Db.SaveChangesAsync();

        var csv = Encoding.UTF8.GetString(
            (await NewService(tdb.NewContext()).ExportCampaignResultsAsync(owner, camp.Id, "csv", default)).Content);

        Assert.Equal(
            "rank,candidate_id,session_id,total_score,result,scored_at,flags,full_name,email,rubric_version,"
            + "policy_version,policy_name,score_fallback,"
            + "answered,total_questions,seed_answered,seed_total,skip_penalty,"
            + "cv_match_score,cv_verification_risk,cv_screening_version,below_cutoff",
            csv.Split('\n')[0].TrimEnd('\r'));
    }
}
