using Isas.CampaignService.DTOs;
using Isas.CampaignService.Models;
using Isas.CampaignService.Services;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging;
using Moq;

namespace Isas.CampaignService.Tests;

/// <summary>
/// CAMP-19 — CHẤM THỬ thước đo.
///
/// <para>Nhóm test nặng nhất là TRẬT TỰ GUARD: mọi guard phải chạy TRƯỚC <c>ReserveAsync</c> (PAY-5).
/// Đảo một bước là org bị trừ credit cho request đằng nào cũng bị từ chối, và để lại chỗ giữ mồ côi —
/// đúng lớp lỗi mà `ClampCampaignMaxQuestions` từng phải đi vá ở đường B2B.</para>
/// </summary>
public class RubricPreviewTests
{
    private const string D0 = "CÓ: không nêu được ý nào | CÒN THIẾU: toàn bộ nội dung";
    private const string DTop = "CÓ: nêu đủ ý, ví dụ, đánh đổi | CÒN THIẾU: không đáng kể";

    private static RubricPreviewService NewService(
        CampaignDbContext db, IRubricPreviewClient? ai = null, ICreditReservationClient? credits = null)
        => new(db, ai ?? Mock.Of<IRubricPreviewClient>(),
            Mock.Of<ILogger<RubricPreviewService>>(), credits);

    private static Mock<IRubricPreviewClient> AiThatWorks(Guid criterionId, int score = 3)
    {
        var ai = new Mock<IRubricPreviewClient>();
        ai.Setup(x => x.RunAsync(It.IsAny<string>(), It.IsAny<string>(), It.IsAny<string?>(),
                It.IsAny<string>(), It.IsAny<string?>(), It.IsAny<string?>(), It.IsAny<int>(),
                It.IsAny<IReadOnlyList<PreviewCriterionInput>>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(new RubricPreviewResult(
                new[] { "Weak", "Good", "Excellent" }.Select(b => new PreviewSample(
                    b, $"bài {b}", 160,
                    new List<PreviewSampleScore> { new(criterionId, score, score, "vì thế này") })).ToList(),
                PromptVersion: 4, LengthParityWarning: false));
        return ai;
    }

    /// <summary>Campaign + 1 câu hỏi + 1 tiêu chí CÓ mốc (đủ điều kiện chạy chấm thử).</summary>
    private static async Task<(Campaign Camp, CampaignCriterion Crit)> SeedReadyAsync(
        CampaignTestDb tdb, Guid owner, CampaignStatus status = CampaignStatus.Draft,
        bool withLevels = true, bool withQuestion = true)
    {
        var camp = CampaignTestDb.NewCampaign(owner, status);
        camp.Domain = "BE";
        tdb.Db.Campaigns.Add(camp);

        if (withQuestion)
            tdb.Db.CampaignQuestions.Add(new CampaignQuestion
            {
                Id = Guid.NewGuid(), CampaignId = camp.Id, QuestionText = "Giải thích index B-tree?",
                Source = QuestionSource.CustomHr, CreatedAt = DateTime.UtcNow
            });

        var cr = new CampaignCriterion
        {
            Id = Guid.NewGuid(), CampaignId = camp.Id, OrderNo = 0, Name = "Chuyên môn",
            Weight = 1.0m, MaxScore = 5, Source = CriterionSource.HrEdited,
            CreatedAt = DateTime.UtcNow, UpdatedAt = DateTime.UtcNow
        };
        tdb.Db.CampaignCriteria.Add(cr);
        if (withLevels)
            tdb.Db.CampaignCriterionLevels.AddRange(
                NewLevel(cr.Id, 0, D0), NewLevel(cr.Id, 5, DTop));

        await tdb.Db.SaveChangesAsync();
        return (camp, cr);
    }

    private static CampaignCriterionLevel NewLevel(Guid criterionId, int score, string descriptor)
        => new()
        {
            Id = Guid.NewGuid(), CriterionId = criterionId, Score = score, Descriptor = descriptor,
            CreatedAt = DateTime.UtcNow, UpdatedAt = DateTime.UtcNow
        };

    private static RubricPreviewRun SeedRun(
        Guid campaignId, RubricPreviewStatus status, int rubricVersion = 1, DateTime? createdAt = null)
        => new()
        {
            Id = Guid.NewGuid(), CampaignId = campaignId, CreatedByUserId = Guid.NewGuid(),
            QuestionText = "q", Status = status, RubricSnapshot = "[]", RubricFingerprint = "fp",
            RubricVersion = rubricVersion, CreatedAt = createdAt ?? DateTime.UtcNow
        };

    // ── TRẬT TỰ GUARD: mọi guard TRƯỚC ReserveAsync ──────────────────────

    // Đối chứng đếm 0 lần gọi Payment. Guard nào chạy sau reserve thì org mất credit cho một request
    // đằng nào cũng bị từ chối, và bỏ lại một chỗ giữ mồ côi.
    public static TheoryData<string> GuardCases() => new() { "closed", "no-criteria", "no-levels", "no-question", "not-owner" };

    [Theory]
    [MemberData(nameof(GuardCases))]
    public async Task Moi_guard_chay_TRUOC_ReserveAsync(string ca)
    {
        using var tdb = new CampaignTestDb();
        var owner = Guid.NewGuid();
        var (camp, _) = await SeedReadyAsync(tdb, owner,
            status: ca == "closed" ? CampaignStatus.Closed : CampaignStatus.Draft,
            withLevels: ca != "no-levels",
            withQuestion: ca != "no-question");

        if (ca == "no-criteria")
        {
            tdb.Db.CampaignCriterionLevels.RemoveRange(tdb.Db.CampaignCriterionLevels);
            tdb.Db.CampaignCriteria.RemoveRange(tdb.Db.CampaignCriteria);
            await tdb.Db.SaveChangesAsync();
        }

        // Strict + VerifyNoOtherCalls: bất kỳ lần chạm Payment nào cũng làm test đỏ.
        var credits = new Mock<ICreditReservationClient>(MockBehavior.Strict);
        var caller = ca == "not-owner" ? Guid.NewGuid() : owner;

        await Assert.ThrowsAnyAsync<Exception>(() =>
            NewService(tdb.NewContext(), credits: credits.Object)
                .RunAsync(caller, owner, camp.Id, new RubricPreviewRequest(), default));

        credits.VerifyNoOtherCalls();

        // Và không để lại row nửa vời.
        using var check = tdb.NewContext();
        Assert.Empty(await check.RubricPreviewRuns.ToListAsync());
    }

    // ── Quota ────────────────────────────────────────────────────────────

    [Fact]
    public async Task Ba_luot_dau_KHONG_tinh_phi()
    {
        using var tdb = new CampaignTestDb();
        var owner = Guid.NewGuid();
        var (camp, cr) = await SeedReadyAsync(tdb, owner);
        var credits = new Mock<ICreditReservationClient>(MockBehavior.Strict);

        for (var i = 0; i < 3; i++)
        {
            var res = await NewService(tdb.NewContext(), AiThatWorks(cr.Id).Object, credits.Object)
                .RunAsync(owner, owner, camp.Id, new RubricPreviewRequest(), default);
            Assert.False(res.Billed);
            Assert.Equal("Succeeded", res.Status);
            Assert.Equal(2 - i, res.FreeRunsRemaining);
        }
        credits.VerifyNoOtherCalls();
    }

    [Fact]
    public async Task Luot_thu_tu_reserve_dung_MOT_lan_voi_khoa_la_id_cua_luot()
    {
        using var tdb = new CampaignTestDb();
        var owner = Guid.NewGuid();
        var (camp, cr) = await SeedReadyAsync(tdb, owner);
        for (var i = 0; i < 3; i++)
            tdb.Db.RubricPreviewRuns.Add(SeedRun(camp.Id, RubricPreviewStatus.Succeeded));
        await tdb.Db.SaveChangesAsync();

        Guid? reservedKey = null;
        var credits = new Mock<ICreditReservationClient>();
        credits.Setup(x => x.ReserveAsync("Org", owner, It.IsAny<Guid>(), It.IsAny<CancellationToken>()))
            .Callback<string, Guid, Guid, CancellationToken>((_, _, k, _) => reservedKey = k)
            .ReturnsAsync(new CreditReservationResult(Guid.NewGuid(), 1));

        var res = await NewService(tdb.NewContext(), AiThatWorks(cr.Id).Object, credits.Object)
            .RunAsync(owner, owner, camp.Id, new RubricPreviewRequest(), default);

        Assert.True(res.Billed);
        credits.Verify(x => x.ReserveAsync("Org", owner, It.IsAny<Guid>(), It.IsAny<CancellationToken>()), Times.Once);
        credits.Verify(x => x.ConsumeAsync(res.Id, It.IsAny<CancellationToken>()), Times.Once);
        Assert.Equal(res.Id, reservedKey);   // khoá idempotency = id lượt chấm thử
    }

    // Chỉ đếm Succeeded: phạt HR vì AI của ta hỏng là sai.
    [Fact]
    public async Task Quota_chi_dem_luot_Succeeded()
    {
        using var tdb = new CampaignTestDb();
        var owner = Guid.NewGuid();
        var (camp, cr) = await SeedReadyAsync(tdb, owner);
        tdb.Db.RubricPreviewRuns.AddRange(
            SeedRun(camp.Id, RubricPreviewStatus.Succeeded),
            SeedRun(camp.Id, RubricPreviewStatus.Succeeded),
            SeedRun(camp.Id, RubricPreviewStatus.Failed),
            SeedRun(camp.Id, RubricPreviewStatus.Failed),
            SeedRun(camp.Id, RubricPreviewStatus.Failed));
        await tdb.Db.SaveChangesAsync();

        var credits = new Mock<ICreditReservationClient>(MockBehavior.Strict);
        var res = await NewService(tdb.NewContext(), AiThatWorks(cr.Id).Object, credits.Object)
            .RunAsync(owner, owner, camp.Id, new RubricPreviewRequest(), default);

        Assert.False(res.Billed);          // 2 Succeeded < 3 ⇒ vẫn free dù đã hỏng 3 lần
        Assert.Equal(0, res.FreeRunsRemaining);
        credits.VerifyNoOtherCalls();
    }

    // Thước đo mới là bài toán mới: campaign chạy 6 tháng sửa thước 4 lần mà dùng chung quota sẽ hết
    // lượt ngay lần hai rồi HR quay về sửa mù.
    [Fact]
    public async Task Bump_version_thi_quota_free_reset()
    {
        using var tdb = new CampaignTestDb();
        var owner = Guid.NewGuid();
        var (camp, cr) = await SeedReadyAsync(tdb, owner);
        for (var i = 0; i < 3; i++)
            tdb.Db.RubricPreviewRuns.Add(SeedRun(camp.Id, RubricPreviewStatus.Succeeded, rubricVersion: 1));
        camp.RubricVersion = 2;
        tdb.Db.Campaigns.Update(camp);
        await tdb.Db.SaveChangesAsync();

        var credits = new Mock<ICreditReservationClient>(MockBehavior.Strict);
        var res = await NewService(tdb.NewContext(), AiThatWorks(cr.Id).Object, credits.Object)
            .RunAsync(owner, owner, camp.Id, new RubricPreviewRequest(), default);

        Assert.False(res.Billed);
        Assert.Equal(2, res.RubricVersion);
        credits.VerifyNoOtherCalls();
    }

    // ── Chống double-click + self-heal ───────────────────────────────────

    [Fact]
    public async Task Dang_co_luot_chay_thi_409()
    {
        using var tdb = new CampaignTestDb();
        var owner = Guid.NewGuid();
        var (camp, cr) = await SeedReadyAsync(tdb, owner);
        tdb.Db.RubricPreviewRuns.Add(SeedRun(camp.Id, RubricPreviewStatus.Running));
        await tdb.Db.SaveChangesAsync();

        var credits = new Mock<ICreditReservationClient>(MockBehavior.Strict);
        await Assert.ThrowsAsync<InvalidOperationException>(() =>
            NewService(tdb.NewContext(), AiThatWorks(cr.Id).Object, credits.Object)
                .RunAsync(owner, owner, camp.Id, new RubricPreviewRequest(), default));
        credits.VerifyNoOtherCalls();
    }

    // Không self-heal thì UNIQUE có điều kiện khoá chết campaign ở 409 VĨNH VIỄN sau một lần tiến
    // trình chết giữa lời gọi đồng bộ.
    [Fact]
    public async Task Luot_Running_mo_coi_qua_5_phut_tu_Failed_roi_chay_tiep()
    {
        using var tdb = new CampaignTestDb();
        var owner = Guid.NewGuid();
        var (camp, cr) = await SeedReadyAsync(tdb, owner);
        var stale = SeedRun(camp.Id, RubricPreviewStatus.Running, createdAt: DateTime.UtcNow.AddMinutes(-6));
        tdb.Db.RubricPreviewRuns.Add(stale);
        await tdb.Db.SaveChangesAsync();

        var res = await NewService(tdb.NewContext(), AiThatWorks(cr.Id).Object)
            .RunAsync(owner, owner, camp.Id, new RubricPreviewRequest(), default);

        Assert.Equal("Succeeded", res.Status);
        using var check = tdb.NewContext();
        Assert.Equal(RubricPreviewStatus.Failed,
            (await check.RubricPreviewRuns.FirstAsync(r => r.Id == stale.Id)).Status);
    }

    // ── AI hỏng ──────────────────────────────────────────────────────────

    [Fact]
    public async Task AI_loi_thi_luot_Failed_release_credit_va_quota_khong_giam()
    {
        using var tdb = new CampaignTestDb();
        var owner = Guid.NewGuid();
        var (camp, cr) = await SeedReadyAsync(tdb, owner);
        for (var i = 0; i < 3; i++)
            tdb.Db.RubricPreviewRuns.Add(SeedRun(camp.Id, RubricPreviewStatus.Succeeded));
        await tdb.Db.SaveChangesAsync();

        var ai = new Mock<IRubricPreviewClient>();
        ai.Setup(x => x.RunAsync(It.IsAny<string>(), It.IsAny<string>(), It.IsAny<string?>(),
                It.IsAny<string>(), It.IsAny<string?>(), It.IsAny<string?>(), It.IsAny<int>(),
                It.IsAny<IReadOnlyList<PreviewCriterionInput>>(), It.IsAny<CancellationToken>()))
            .ThrowsAsync(new DownstreamServiceException("AIService sập"));

        var credits = new Mock<ICreditReservationClient>();
        credits.Setup(x => x.ReserveAsync(It.IsAny<string>(), It.IsAny<Guid>(), It.IsAny<Guid>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(new CreditReservationResult(Guid.NewGuid(), 1));

        await Assert.ThrowsAsync<DownstreamServiceException>(() =>
            NewService(tdb.NewContext(), ai.Object, credits.Object)
                .RunAsync(owner, owner, camp.Id, new RubricPreviewRequest(), default));

        using var check = tdb.NewContext();
        var run = await check.RubricPreviewRuns.OrderByDescending(r => r.CreatedAt).FirstAsync();
        Assert.Equal(RubricPreviewStatus.Failed, run.Status);
        Assert.Contains("AIService sập", run.ErrorReason);
        credits.Verify(x => x.ReleaseAsync(run.Id, It.IsAny<CancellationToken>()), Times.Once);
        credits.Verify(x => x.ConsumeAsync(It.IsAny<Guid>(), It.IsAny<CancellationToken>()), Times.Never);
        // Vẫn đúng 3 lượt Succeeded ⇒ HR không mất lượt free vì AI của ta hỏng.
        Assert.Equal(3, await check.RubricPreviewRuns.CountAsync(r => r.Status == RubricPreviewStatus.Succeeded));
    }

    // Ví org hết credit ⇒ 402, và lượt đó đánh dấu Failed chứ không nằm Running khoá campaign.
    [Fact]
    public async Task Vi_org_het_credit_thi_402_va_khong_ket_luot_Running()
    {
        using var tdb = new CampaignTestDb();
        var owner = Guid.NewGuid();
        var (camp, cr) = await SeedReadyAsync(tdb, owner);
        for (var i = 0; i < 3; i++)
            tdb.Db.RubricPreviewRuns.Add(SeedRun(camp.Id, RubricPreviewStatus.Succeeded));
        await tdb.Db.SaveChangesAsync();

        var credits = new Mock<ICreditReservationClient>();
        credits.Setup(x => x.ReserveAsync(It.IsAny<string>(), It.IsAny<Guid>(), It.IsAny<Guid>(), It.IsAny<CancellationToken>()))
            .ThrowsAsync(new InsufficientOrgCreditException("hết credit"));

        await Assert.ThrowsAsync<InsufficientOrgCreditException>(() =>
            NewService(tdb.NewContext(), AiThatWorks(cr.Id).Object, credits.Object)
                .RunAsync(owner, owner, camp.Id, new RubricPreviewRequest(), default));

        using var check = tdb.NewContext();
        Assert.Empty(await check.RubricPreviewRuns.Where(r => r.Status == RubricPreviewStatus.Running).ToListAsync());
    }

    // ── Nội dung kết quả ─────────────────────────────────────────────────

    [Fact]
    public async Task Ket_qua_dong_dau_van_tay_phien_ban_va_prompt()
    {
        using var tdb = new CampaignTestDb();
        var owner = Guid.NewGuid();
        var (camp, cr) = await SeedReadyAsync(tdb, owner);

        var res = await NewService(tdb.NewContext(), AiThatWorks(cr.Id).Object)
            .RunAsync(owner, owner, camp.Id, new RubricPreviewRequest(), default);

        Assert.Equal(RubricFingerprint.Compute(new[] { WithLevels(cr) }), res.RubricFingerprint);
        Assert.Equal(1, res.RubricVersion);
        Assert.Equal(4, res.PromptVersion);
        // v1: bài mẫu là văn bản ⇒ KHÔNG có số đo cách nói (F11). Cờ cấu trúc, không giấu.
        Assert.False(res.DeliveryMetricsAvailable);
        Assert.Equal(3, res.Samples.Count);
        Assert.Equal(new[] { "Weak", "Good", "Excellent" }, res.Samples.Select(s => s.Band));
        // Thước đo ĐÃ DÙNG đi kèm kết quả (snapshot), không phải bộ hiện tại.
        Assert.Equal(new[] { 0, 5 }, Assert.Single(res.Rubric).Levels.Select(l => l.Score));
    }

    // Δ (kỳ vọng vs thật) là số đo DUY NHẤT về độ chệch tự-khen-văn-mình mà một model đơn cho được.
    [Fact]
    public async Task Bao_cao_ky_vong_vs_thuc_te()
    {
        using var tdb = new CampaignTestDb();
        var owner = Guid.NewGuid();
        var (camp, cr) = await SeedReadyAsync(tdb, owner);

        // Thang 2 mốc {0,5}: kỳ vọng Weak = mốc 0, Excellent = mốc 5. AI chấm cả 3 bài đều 5 điểm
        // ⇒ bài Weak có Δ = +100 điểm phần trăm: model đang tự khen văn nó viết.
        var res = await NewService(tdb.NewContext(), AiThatWorks(cr.Id, score: 5).Object)
            .RunAsync(owner, owner, camp.Id, new RubricPreviewRequest(), default);

        var weak = res.Samples.First(s => s.Band == "Weak");
        Assert.Equal(0m, weak.ExpectedWeightedPct);
        Assert.Equal(100m, weak.ActualWeightedPct);
        Assert.Equal(0, Assert.Single(weak.Scores).ExpectedLevel);
        Assert.Equal(5m, Assert.Single(weak.Scores).ActualScore);
        Assert.Equal("Chuyên môn", Assert.Single(weak.Scores).CriterionName);
    }

    [Fact]
    public async Task Cau_hoi_ngoai_chien_dich_thi_400()
    {
        using var tdb = new CampaignTestDb();
        var owner = Guid.NewGuid();
        var (camp, cr) = await SeedReadyAsync(tdb, owner);

        await Assert.ThrowsAsync<ArgumentException>(() =>
            NewService(tdb.NewContext(), AiThatWorks(cr.Id).Object)
                .RunAsync(owner, owner, camp.Id, new RubricPreviewRequest { QuestionId = Guid.NewGuid() }, default));
    }

    [Fact]
    public async Task Lich_su_tra_moi_nhat_truoc()
    {
        using var tdb = new CampaignTestDb();
        var owner = Guid.NewGuid();
        var (camp, _) = await SeedReadyAsync(tdb, owner);
        var cu = SeedRun(camp.Id, RubricPreviewStatus.Succeeded, createdAt: DateTime.UtcNow.AddHours(-2));
        var moi = SeedRun(camp.Id, RubricPreviewStatus.Succeeded, createdAt: DateTime.UtcNow);
        tdb.Db.RubricPreviewRuns.AddRange(cu, moi);
        await tdb.Db.SaveChangesAsync();

        var res = await NewService(tdb.NewContext()).GetHistoryAsync(owner, camp.Id, default);
        Assert.Equal(new[] { moi.Id, cu.Id }, res.Select(r => r.Id));
    }

    [Fact]
    public async Task Lich_su_cua_campaign_ngoai_org_thi_404()
    {
        using var tdb = new CampaignTestDb();
        var (camp, _) = await SeedReadyAsync(tdb, Guid.NewGuid());
        await Assert.ThrowsAsync<KeyNotFoundException>(() =>
            NewService(tdb.NewContext()).GetHistoryAsync(Guid.NewGuid(), camp.Id, default));
    }

    private static CampaignCriterion WithLevels(CampaignCriterion cr)
    {
        cr.Levels = new List<CampaignCriterionLevel> { NewLevel(cr.Id, 0, D0), NewLevel(cr.Id, 5, DTop) };
        return cr;
    }
}
