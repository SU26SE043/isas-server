using Isas.CampaignService.DTOs;
using Isas.CampaignService.Models;
using Isas.CampaignService.Services;
using Microsoft.Extensions.Logging;
using Moq;

using CampaignSvc = Isas.CampaignService.Services.CampaignService;

namespace Isas.CampaignService.Tests;

/// <summary>
/// E11b — HR chốt/sửa điểm-kết-quả cuối (điểm AI = gợi ý). Override ghi cột trên campaign_rankings;
/// results đọc effective (override ?? AI) → điểm/rank/pass-fail đổi theo; clear = về AI; audit ghi;
/// ngoài org → 404; session không có ranking → 404; Note bắt buộc; Result chỉ Pass/Fail.
/// </summary>
public class ResultOverrideE11bTests
{
    private static CampaignSvc NewService(CampaignDbContext db) =>
        new(db, Mock.Of<IFileService>(), Mock.Of<ILogger<CampaignSvc>>(),
            Mock.Of<IParserService>(), Mock.Of<ICriteriaSuggester>(),
            Mock.Of<IInvitationEmailPublisher>());

    private static Campaign SeedCampaign(CampaignDbContext db, Guid orgId, int? pass = null)
    {
        var c = CampaignTestDb.NewCampaign(orgId, CampaignStatus.Active);
        c.PassScorePct = pass;
        db.Campaigns.Add(c);
        db.SaveChanges();
        return c;
    }

    private static CampaignRanking SeedRanking(CampaignDbContext db, Guid campaignId, decimal score, Guid? sessionId = null)
    {
        var r = new CampaignRanking
        {
            Id = Guid.NewGuid(),
            CampaignId = campaignId,
            CandidateId = Guid.NewGuid(),
            SessionId = sessionId ?? Guid.NewGuid(),
            TotalScore = score,
            UpdatedAt = DateTime.UtcNow
        };
        db.CampaignRankings.Add(r);
        db.SaveChanges();
        return r;
    }

    [Fact]
    public async Task Override_score_changes_effective_score_and_rank_and_records_audit()
    {
        using var tdb = new CampaignTestDb();
        var orgId = Guid.NewGuid();
        var actor = Guid.NewGuid();
        var campaign = SeedCampaign(tdb.Db, orgId, pass: 50);
        var low = SeedRanking(tdb.Db, campaign.Id, 40.00m);   // AI Fail, rank 2
        var high = SeedRanking(tdb.Db, campaign.Id, 80.00m);  // AI Pass, rank 1

        // HR đẩy 'low' lên 95 + Pass → phải vượt 'high'.
        await NewService(tdb.NewContext()).OverrideResultAsync(
            orgId, actor, campaign.Id, low.SessionId,
            new OverrideResultRequest { Score = 95.00m, Result = "Pass", Note = "Phỏng vấn trực tiếp rất tốt" }, default);

        var res = await NewService(tdb.NewContext()).GetCampaignResultsAsync(orgId, campaign.Id, default);
        var top = res.Results[0];
        Assert.Equal(low.SessionId, top.SessionId);       // low lên #1
        Assert.Equal(1, top.Rank);
        Assert.Equal(95.00m, top.TotalScore);             // effective
        Assert.Equal(40.00m, top.AiScore);                // AI gốc giữ nguyên
        Assert.Equal("Pass", top.Result);
        Assert.Equal(95.00m, top.OverrideScore);
        Assert.NotNull(top.OverriddenAt);

        // audit ghi 1 dòng OverrideResult
        using var verify = tdb.NewContext();
        Assert.Contains(verify.AuditLogs, a => a.Action == AuditAction.OverrideResult && a.EntityId == campaign.Id && a.ActorUserId == actor);
    }

    [Fact]
    public async Task Override_result_only_forces_passfail_over_threshold()
    {
        using var tdb = new CampaignTestDb();
        var orgId = Guid.NewGuid();
        var campaign = SeedCampaign(tdb.Db, orgId, pass: 50);
        var r = SeedRanking(tdb.Db, campaign.Id, 80.00m);   // AI Pass theo ngưỡng

        // HR ép Fail dù điểm 80 ≥ 50.
        await NewService(tdb.NewContext()).OverrideResultAsync(
            orgId, Guid.NewGuid(), campaign.Id, r.SessionId,
            new OverrideResultRequest { Result = "Fail", Note = "Phát hiện gian lận" }, default);

        var res = await NewService(tdb.NewContext()).GetCampaignResultsAsync(orgId, campaign.Id, default);
        Assert.Equal("Fail", res.Results[0].Result);
        Assert.Equal(80.00m, res.Results[0].TotalScore);   // điểm không đổi (chỉ ép result)
    }

    [Fact]
    public async Task Clear_override_reverts_to_ai()
    {
        using var tdb = new CampaignTestDb();
        var orgId = Guid.NewGuid();
        var campaign = SeedCampaign(tdb.Db, orgId, pass: 50);
        var r = SeedRanking(tdb.Db, campaign.Id, 40.00m);

        await NewService(tdb.NewContext()).OverrideResultAsync(
            orgId, Guid.NewGuid(), campaign.Id, r.SessionId,
            new OverrideResultRequest { Score = 90m, Result = "Pass", Note = "tốt" }, default);
        // Clear
        await NewService(tdb.NewContext()).OverrideResultAsync(
            orgId, Guid.NewGuid(), campaign.Id, r.SessionId,
            new OverrideResultRequest { Score = null, Result = null, Note = "huỷ điều chỉnh" }, default);

        var res = await NewService(tdb.NewContext()).GetCampaignResultsAsync(orgId, campaign.Id, default);
        Assert.Equal(40.00m, res.Results[0].TotalScore);   // về AI
        Assert.Null(res.Results[0].OverrideScore);
        Assert.Equal("Fail", res.Results[0].Result);        // theo ngưỡng lại
    }

    [Fact]
    public async Task Override_outside_org_throws_404()
    {
        using var tdb = new CampaignTestDb();
        var campaign = SeedCampaign(tdb.Db, Guid.NewGuid());
        var r = SeedRanking(tdb.Db, campaign.Id, 50m);

        await Assert.ThrowsAsync<KeyNotFoundException>(() =>
            NewService(tdb.NewContext()).OverrideResultAsync(
                Guid.NewGuid() /* org khác */, Guid.NewGuid(), campaign.Id, r.SessionId,
                new OverrideResultRequest { Score = 90m, Note = "x" }, default));
    }

    [Fact]
    public async Task Override_missing_ranking_throws_404()
    {
        using var tdb = new CampaignTestDb();
        var orgId = Guid.NewGuid();
        var campaign = SeedCampaign(tdb.Db, orgId);

        await Assert.ThrowsAsync<KeyNotFoundException>(() =>
            NewService(tdb.NewContext()).OverrideResultAsync(
                orgId, Guid.NewGuid(), campaign.Id, Guid.NewGuid() /* session không có ranking */,
                new OverrideResultRequest { Score = 90m, Note = "x" }, default));
    }

    [Theory]
    [InlineData("", "Pass")]        // Note rỗng
    [InlineData("lý do", "Maybe")]  // Result sai
    public async Task Override_invalid_input_throws_argument(string note, string result)
    {
        using var tdb = new CampaignTestDb();
        var orgId = Guid.NewGuid();
        var campaign = SeedCampaign(tdb.Db, orgId);
        var r = SeedRanking(tdb.Db, campaign.Id, 50m);

        await Assert.ThrowsAsync<ArgumentException>(() =>
            NewService(tdb.NewContext()).OverrideResultAsync(
                orgId, Guid.NewGuid(), campaign.Id, r.SessionId,
                new OverrideResultRequest { Score = 90m, Result = result, Note = note }, default));
    }

    // ───────── Q12 — điểm override phải CÙNG THANG phần trăm với điểm AI + ngưỡng đạt ─────────
    //
    // `GetCampaignResultsAsync` so thẳng `effectiveScore >= PassScorePct`. `total_score` (Interview gửi)
    // đã là % [0,100] và `pass_score_pct` bị ValidatePassScorePct ép về [0,100] — chỉ cột override là
    // nhận mọi decimal ⇒ trước Q12, giá trị lệch thang ghi được và làm hỏng pass/fail trong im lặng.

    [Theory]
    [InlineData(-0.01)]
    [InlineData(-1)]
    [InlineData(100.01)]
    [InlineData(101)]
    [InlineData(1000)]      // vượt cả numeric(5,2) → trước Q12 là DbUpdateException (500) trên Postgres
    public async Task Q12_Diem_override_ngoai_dai_0_100_thi_400_va_KHONG_ghi(double score)
    {
        using var tdb = new CampaignTestDb();
        var orgId = Guid.NewGuid();
        var campaign = SeedCampaign(tdb.Db, orgId, pass: 50);
        var r = SeedRanking(tdb.Db, campaign.Id, 40.00m);

        // Controller bắt ArgumentException → 400 (KHÔNG rơi xuống catch(Exception) → 500).
        await Assert.ThrowsAsync<ArgumentException>(() =>
            NewService(tdb.NewContext()).OverrideResultAsync(
                orgId, Guid.NewGuid(), campaign.Id, r.SessionId,
                new OverrideResultRequest { Score = (decimal)score, Result = "Pass", Note = "lý do" }, default));

        // Chặn phải xảy ra TRƯỚC khi ghi: ranking giữ nguyên, không có override một-nửa.
        using var check = tdb.NewContext();
        var row = check.CampaignRankings.Single(x => x.SessionId == r.SessionId);
        Assert.Null(row.OverrideScore);
        Assert.Null(row.OverrideResult);
        Assert.Null(row.OverriddenAt);
    }

    [Theory]
    [InlineData(0)]         // biên dưới — "0%" là điểm hợp lệ, không được chặn nhầm
    [InlineData(100)]       // biên trên
    public async Task Q12_Diem_override_o_bien_0_va_100_van_ghi_duoc(double score)
    {
        using var tdb = new CampaignTestDb();
        var orgId = Guid.NewGuid();
        var campaign = SeedCampaign(tdb.Db, orgId, pass: 50);
        var r = SeedRanking(tdb.Db, campaign.Id, 40.00m);

        await NewService(tdb.NewContext()).OverrideResultAsync(
            orgId, Guid.NewGuid(), campaign.Id, r.SessionId,
            new OverrideResultRequest { Score = (decimal)score, Note = "chốt tay" }, default);

        var res = await NewService(tdb.NewContext()).GetCampaignResultsAsync(orgId, campaign.Id, default);
        Assert.Equal((decimal)score, res.Results[0].OverrideScore);
        // Cùng thang với ngưỡng 50 → 0 là Fail, 100 là Pass. Đây chính là bất biến Q12 bảo vệ.
        Assert.Equal(score >= 50 ? "Pass" : "Fail", res.Results[0].Result);
    }

    [Fact]
    public async Task Q12_Clear_override_khong_bi_guard_diem_chan()
    {
        using var tdb = new CampaignTestDb();
        var orgId = Guid.NewGuid();
        var campaign = SeedCampaign(tdb.Db, orgId, pass: 50);
        var r = SeedRanking(tdb.Db, campaign.Id, 40.00m);

        // Score = null (huỷ override) không phải "ngoài dải" → guard phải bỏ qua, không ném.
        await NewService(tdb.NewContext()).OverrideResultAsync(
            orgId, Guid.NewGuid(), campaign.Id, r.SessionId,
            new OverrideResultRequest { Score = null, Result = null, Note = "huỷ" }, default);

        using var check = tdb.NewContext();
        Assert.Null(check.CampaignRankings.Single(x => x.SessionId == r.SessionId).OverrideScore);
    }

    [Fact]
    public async Task Q12_Diem_8_van_hop_le_vi_8_phan_tram_la_that_KHONG_suy_doan_thang_khac()
    {
        using var tdb = new CampaignTestDb();
        var orgId = Guid.NewGuid();
        var campaign = SeedCampaign(tdb.Db, orgId, pass: 50);
        var r = SeedRanking(tdb.Db, campaign.Id, 40.00m);

        await NewService(tdb.NewContext()).OverrideResultAsync(
            orgId, Guid.NewGuid(), campaign.Id, r.SessionId,
            new OverrideResultRequest { Score = 8m, Note = "chốt tay" }, default);

        var res = await NewService(tdb.NewContext()).GetCampaignResultsAsync(orgId, campaign.Id, default);

        // Khoá CÓ CHỦ ĐÍCH: 8 được hiểu là 8% ⇒ Fail so với ngưỡng 50.
        // HR gõ 8 với ý "8/10" là hiểu nhầm THANG, và BE không có cách nào phân biệt với 8% thật.
        // Test này chặn "bản vá thông minh" kiểu `if (score <= 10) score *= 10` — heuristic đó sẽ âm thầm
        // biến điểm 8% thật thành 80% (Pass). Lời giải đúng nằm ở UI: nhãn "%" + min/max trên ô nhập.
        Assert.Equal(8m, res.Results[0].OverrideScore);
        Assert.Equal("Fail", res.Results[0].Result);
    }

    [Fact]
    public async Task Q12_Diem_AI_da_la_phan_tram_nen_so_thang_voi_nguong_la_dung()
    {
        using var tdb = new CampaignTestDb();
        var orgId = Guid.NewGuid();
        var campaign = SeedCampaign(tdb.Db, orgId, pass: 50);
        // 80.00 là điểm AI = 80% (Interview đã chuẩn hoá pct trước khi gộp trọng số), KHÔNG phải 80 điểm thô.
        var pass = SeedRanking(tdb.Db, campaign.Id, 80.00m);
        var fail = SeedRanking(tdb.Db, campaign.Id, 49.99m);

        var res = await NewService(tdb.NewContext()).GetCampaignResultsAsync(orgId, campaign.Id, default);

        Assert.Equal("Pass", res.Results.Single(x => x.SessionId == pass.SessionId).Result);
        Assert.Equal("Fail", res.Results.Single(x => x.SessionId == fail.SessionId).Result);
    }

    [Fact]
    public async Task Override_result_Pass_thang_nguong_khi_diem_duoi_nguong()
    {
        using var tdb = new CampaignTestDb();
        var orgId = Guid.NewGuid();
        var campaign = SeedCampaign(tdb.Db, orgId, pass: 50);
        var r = SeedRanking(tdb.Db, campaign.Id, 20.00m);   // AI Fail theo ngưỡng

        // Chiều ngược lại của Override_result_only_forces_passfail_over_threshold: HR ép Pass dù 20 < 50.
        // Hai test cùng khoá "OverrideResult THẮNG ngưỡng" ở CẢ HAI chiều (CAMP-11) — đảo thứ tự ưu tiên
        // trong GetCampaignResultsAsync sẽ làm ít nhất một trong hai đỏ dù seed nghiêng về phía nào.
        await NewService(tdb.NewContext()).OverrideResultAsync(
            orgId, Guid.NewGuid(), campaign.Id, r.SessionId,
            new OverrideResultRequest { Result = "Pass", Note = "Có kinh nghiệm thực tế bù lại" }, default);

        var res = await NewService(tdb.NewContext()).GetCampaignResultsAsync(orgId, campaign.Id, default);
        Assert.Equal("Pass", res.Results[0].Result);
        Assert.Equal(20.00m, res.Results[0].TotalScore);   // điểm không đổi (chỉ ép result)
    }

    [Fact]
    public async Task Nguong_null_thi_Result_null_du_co_diem_override()
    {
        using var tdb = new CampaignTestDb();
        var orgId = Guid.NewGuid();
        var campaign = SeedCampaign(tdb.Db, orgId, pass: null);   // HR chưa khai ngưỡng → quyết tay
        var r = SeedRanking(tdb.Db, campaign.Id, 40.00m);

        await NewService(tdb.NewContext()).OverrideResultAsync(
            orgId, Guid.NewGuid(), campaign.Id, r.SessionId,
            new OverrideResultRequest { Score = 95m, Note = "chốt tay" }, default);

        var res = await NewService(tdb.NewContext()).GetCampaignResultsAsync(orgId, campaign.Id, default);
        // Không ngưỡng thì KHÔNG được tự suy Pass/Fail (CAMP-11) — kể cả khi HR đã chốt điểm.
        Assert.Null(res.Results[0].Result);
        Assert.Equal(95m, res.Results[0].TotalScore);
    }
}
