using Isas.InterviewService.ApplicationDbContext;
using Isas.InterviewService.DTOs;
using Isas.InterviewService.Entities;
using Isas.InterviewService.Enums;
using Isas.InterviewService.Services;
using Isas.InterviewService.Services.Interfaces;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging.Abstractions;
using Moq;

namespace Isas.InterviewService.Tests;

/// <summary>
/// BC16 — Rubric CÁ NHÂN B2C: candidate tự CRUD rubric theo JobCategory (không admin).
/// Kiểm: CRUD + validate + soft-versioned + FK-safe · resolver ưu-tiên-riêng-else-mặc-định (khoá 4 site
/// scoring không lệch) · publish/breakdown dùng đúng rubric của candidate; không custom → seed như cũ.
/// </summary>
public class RubricLibraryTests
{
    private static RubricLibraryService Svc(InterviewDbContext db) => new(db);

    // Seed 2 tiêu chí mặc định (candidate_id NULL) cho 1 nghề — mô phỏng trạng thái sau seed BC11.
    private static async Task SeedDefaultAsync(InterviewDbContext db, JobCategory cat)
    {
        db.RubricCriteria.AddRange(
            DefaultCrit(cat, "Default-A", 0.5m),
            DefaultCrit(cat, "Default-B", 0.5m));
        await db.SaveChangesAsync();
    }

    private static RubricCriterion DefaultCrit(JobCategory cat, string name, decimal weight)
        => new()
        {
            Id = Guid.NewGuid(), Name = name, Description = name, Weight = weight,
            MaxScore = 5, IsActive = true, JobCategory = cat, CampaignId = null,
            CandidateId = null, Version = 1
        };

    private static UpsertRubricRequest TwoCriteria(string a = "My-A", string b = "My-B")
        => new([
            new RubricCriterionInput(a, "desc a", 0.6m, 5),
            new RubricCriterionInput(b, "desc b", 0.4m, 10)
        ]);

    // ── CRUD ─────────────────────────────────────────────────────────────────

    [Fact]
    public async Task Replace_CreatesActiveCustomRubric_GetReturnsIt_IsCustom()
    {
        using var t = new TestDb();
        await SeedDefaultAsync(t.Db, JobCategory.BE);
        var me = Guid.NewGuid();

        var res = await Svc(t.Db).ReplaceAsync(me, JobCategory.BE, TwoCriteria());

        Assert.True(res.IsCustom);
        Assert.Equal(2, res.Criteria.Count);

        var got = await Svc(t.Db).GetEffectiveAsync(me, JobCategory.BE);
        Assert.True(got.IsCustom);
        Assert.Equal(new[] { "My-A", "My-B" }.OrderBy(x => x),
                     got.Criteria.Select(c => c.Name).OrderBy(x => x));

        // Rows lưu đúng owner + active + campaign null.
        var rows = await t.Db.RubricCriteria.AsNoTracking()
            .Where(c => c.CandidateId == me && c.JobCategory == JobCategory.BE && c.IsActive).ToListAsync();
        Assert.Equal(2, rows.Count);
        Assert.All(rows, r => Assert.Null(r.CampaignId));
    }

    // BK36 — chuỗi RỖNG cho `language` là một GIÁ TRỊ SAI, KHÔNG được coi như "không gửi". Ở SERVICE
    // NÀY hậu quả nặng hơn PracticeService/RoadmapService: `language` là BỘ CHỌN HÀNG cho
    // `ReplaceAsync` (`c.Language == lang`), và `ReplaceAsync` DEACTIVATE bản đang active khớp bộ
    // chọn đó rồi mới tạo bản mới. Nuốt `""` thành "vi" ⇒ candidate định thay rubric EN mà gõ nhầm
    // (hoặc client gửi query `?language=` rỗng) sẽ deactivate NHẦM rubric VI đang dùng.
    [Fact]
    public async Task Replace_EmptyLanguage_Throws_DoesNotDeactivateExistingViRubric()
    {
        using var t = new TestDb();
        var me = Guid.NewGuid();

        // Rubric "vi" đang active — nếu guard bị nuốt, request "" sẽ rơi vào nhánh "vi" và
        // deactivate đúng bản này.
        await Svc(t.Db).ReplaceAsync(me, JobCategory.BE, TwoCriteria("vi-A", "vi-B"), language: "vi");

        await Assert.ThrowsAsync<InvalidOperationException>(
            () => Svc(t.Db).ReplaceAsync(me, JobCategory.BE, TwoCriteria("evil-A", "evil-B"), language: ""));

        // Rubric "vi" PHẢI còn nguyên active — không có bản "evil-*" nào được tạo.
        var rows = await t.Db.RubricCriteria.AsNoTracking()
            .Where(c => c.CandidateId == me && c.JobCategory == JobCategory.BE).ToListAsync();
        Assert.Equal(2, rows.Count);
        Assert.All(rows, r => Assert.True(r.IsActive));
        Assert.All(rows, r => Assert.Equal("vi", r.Language));
        Assert.DoesNotContain(rows, r => r.Name.StartsWith("evil-"));
    }

    // Null vẫn giữ nghĩa "không gửi" → mặc định "vi" — đối chứng dương cho test rỗng ở trên.
    [Fact]
    public async Task GetEffective_NullLanguage_DefaultsToVi()
    {
        using var t = new TestDb();
        await SeedDefaultAsync(t.Db, JobCategory.BE);

        var got = await Svc(t.Db).GetEffectiveAsync(Guid.NewGuid(), JobCategory.BE, language: null);

        Assert.False(got.IsCustom);
        Assert.Equal(2, got.Criteria.Count);
    }

    [Fact]
    public async Task GetEffective_EmptyLanguage_Throws()
    {
        using var t = new TestDb();
        await Assert.ThrowsAsync<InvalidOperationException>(
            () => Svc(t.Db).GetEffectiveAsync(Guid.NewGuid(), JobCategory.BE, language: ""));
    }

    [Fact]
    public async Task Reset_EmptyLanguage_Throws_DoesNotDeactivateExistingViRubric()
    {
        using var t = new TestDb();
        var me = Guid.NewGuid();
        await Svc(t.Db).ReplaceAsync(me, JobCategory.BE, TwoCriteria("vi-A", "vi-B"), language: "vi");

        await Assert.ThrowsAsync<InvalidOperationException>(
            () => Svc(t.Db).ResetAsync(me, JobCategory.BE, language: ""));

        var rows = await t.Db.RubricCriteria.AsNoTracking()
            .Where(c => c.CandidateId == me && c.JobCategory == JobCategory.BE).ToListAsync();
        Assert.All(rows, r => Assert.True(r.IsActive));
    }

    [Fact]
    public async Task Replace_Twice_DeactivatesOld_BumpsVersion_OnlyNewActive()
    {
        using var t = new TestDb();
        var me = Guid.NewGuid();

        await Svc(t.Db).ReplaceAsync(me, JobCategory.BE, TwoCriteria("v1-A", "v1-B"));
        await Svc(t.Db).ReplaceAsync(me, JobCategory.BE, TwoCriteria("v2-A", "v2-B"));

        var all = await t.Db.RubricCriteria.AsNoTracking()
            .Where(c => c.CandidateId == me && c.JobCategory == JobCategory.BE).ToListAsync();
        var active = all.Where(c => c.IsActive).ToList();
        var inactive = all.Where(c => !c.IsActive).ToList();

        Assert.Equal(2, active.Count);
        Assert.Equal(2, inactive.Count);                       // bản cũ giữ lại (FK-safe), chỉ deactivate
        Assert.All(active, c => Assert.Equal(2, c.Version));    // bump version
        Assert.All(inactive, c => Assert.Equal(1, c.Version));
        Assert.All(active, c => Assert.StartsWith("v2-", c.Name));
    }

    [Theory]
    [InlineData(0)]      // rỗng
    public async Task Replace_Empty_Throws(int count)
    {
        using var t = new TestDb();
        var req = new UpsertRubricRequest(
            Enumerable.Range(0, count).Select(i => new RubricCriterionInput($"c{i}", null, 1m, 5)).ToList());
        await Assert.ThrowsAsync<InvalidOperationException>(
            () => Svc(t.Db).ReplaceAsync(Guid.NewGuid(), JobCategory.BE, req));
    }

    [Fact]
    public async Task Replace_WeightSumOutOfRange_Throws()
    {
        using var t = new TestDb();
        var req = new UpsertRubricRequest([
            new RubricCriterionInput("A", null, 0.9m, 5),
            new RubricCriterionInput("B", null, 0.9m, 5)   // Σ=1.8 ngoài [0.99,1.01]
        ]);
        await Assert.ThrowsAsync<InvalidOperationException>(
            () => Svc(t.Db).ReplaceAsync(Guid.NewGuid(), JobCategory.BE, req));
    }

    [Fact]
    public async Task Replace_DuplicateName_Throws()
    {
        using var t = new TestDb();
        var req = new UpsertRubricRequest([
            new RubricCriterionInput("Same", null, 0.5m, 5),
            new RubricCriterionInput("same", null, 0.5m, 5)   // trùng (case-insensitive)
        ]);
        await Assert.ThrowsAsync<InvalidOperationException>(
            () => Svc(t.Db).ReplaceAsync(Guid.NewGuid(), JobCategory.BE, req));
    }

    [Theory]
    [InlineData(0)]     // maxScore < 1
    [InlineData(-3)]
    public async Task Replace_MaxScoreBelowOne_Throws(int maxScore)
    {
        using var t = new TestDb();
        var req = new UpsertRubricRequest([new RubricCriterionInput("A", null, 1m, maxScore)]);
        await Assert.ThrowsAsync<InvalidOperationException>(
            () => Svc(t.Db).ReplaceAsync(Guid.NewGuid(), JobCategory.BE, req));
    }

    [Theory]
    [InlineData(0)]        // weight ≤ 0
    [InlineData(1.5)]      // weight > 1
    public async Task Replace_WeightOutOfRange_Throws(double weight)
    {
        using var t = new TestDb();
        var req = new UpsertRubricRequest([new RubricCriterionInput("A", null, (decimal)weight, 5)]);
        await Assert.ThrowsAsync<InvalidOperationException>(
            () => Svc(t.Db).ReplaceAsync(Guid.NewGuid(), JobCategory.BE, req));
    }

    [Fact]
    public async Task Replace_NormalizesWeights_SumApproxOne()
    {
        using var t = new TestDb();
        var me = Guid.NewGuid();
        // Σ = 1.0 nhưng chia lệch → sau chuẩn hoá vẫn ≈ 1.
        var req = new UpsertRubricRequest([
            new RubricCriterionInput("A", null, 0.7m, 5),
            new RubricCriterionInput("B", null, 0.3m, 5)
        ]);
        var res = await Svc(t.Db).ReplaceAsync(me, JobCategory.BE, req);
        Assert.InRange(res.Criteria.Sum(c => c.Weight), 0.999m, 1.001m);
    }

    // ── GET fallback + isolation ──────────────────────────────────────────────

    [Fact]
    public async Task GetEffective_NoCustom_ReturnsSeedDefault_IsCustomFalse()
    {
        using var t = new TestDb();
        await SeedDefaultAsync(t.Db, JobCategory.BE);

        var got = await Svc(t.Db).GetEffectiveAsync(Guid.NewGuid(), JobCategory.BE);

        Assert.False(got.IsCustom);
        Assert.Equal(2, got.Criteria.Count);
        Assert.All(got.Criteria, c => Assert.StartsWith("Default-", c.Name));
    }

    [Fact]
    public async Task GetEffective_Isolation_OtherCandidate_SeesDefault_NotMine()
    {
        using var t = new TestDb();
        await SeedDefaultAsync(t.Db, JobCategory.BE);
        var me = Guid.NewGuid();
        var other = Guid.NewGuid();
        await Svc(t.Db).ReplaceAsync(me, JobCategory.BE, TwoCriteria());

        var mine = await Svc(t.Db).GetEffectiveAsync(me, JobCategory.BE);
        var theirs = await Svc(t.Db).GetEffectiveAsync(other, JobCategory.BE);

        Assert.True(mine.IsCustom);
        Assert.False(theirs.IsCustom);       // người khác KHÔNG thấy rubric của tôi
        Assert.All(theirs.Criteria, c => Assert.StartsWith("Default-", c.Name));
    }

    [Fact]
    public async Task Reset_DeactivatesCustom_FallsBackToDefault()
    {
        using var t = new TestDb();
        await SeedDefaultAsync(t.Db, JobCategory.BE);
        var me = Guid.NewGuid();
        await Svc(t.Db).ReplaceAsync(me, JobCategory.BE, TwoCriteria());

        await Svc(t.Db).ResetAsync(me, JobCategory.BE);

        var got = await Svc(t.Db).GetEffectiveAsync(me, JobCategory.BE);
        Assert.False(got.IsCustom);          // quay về mặc định
        // Không hard-delete: bản cũ vẫn còn nhưng inactive.
        Assert.Equal(2, await t.Db.RubricCriteria.AsNoTracking()
            .CountAsync(c => c.CandidateId == me && !c.IsActive));
    }

    [Fact]
    public async Task Reset_NoCustom_IsNoOp()
    {
        using var t = new TestDb();
        var me = Guid.NewGuid();
        await Svc(t.Db).ResetAsync(me, JobCategory.BE);   // không có gì để xoá
        Assert.Empty(await t.Db.RubricCriteria.AsNoTracking().Where(c => c.CandidateId == me).ToListAsync());
    }

    // ── Resolver (seam dùng chung 4 site scoring) ─────────────────────────────

    [Fact]
    public async Task ResolveOwner_NoCustom_ReturnsNull()
    {
        using var t = new TestDb();
        await SeedDefaultAsync(t.Db, JobCategory.BE);
        Assert.Null(await B2CRubricScope.ResolveOwnerAsync(t.Db, Guid.NewGuid(), JobCategory.BE, "vi"));
    }

    [Fact]
    public async Task ResolveOwner_WithActiveCustom_ReturnsCandidateId()
    {
        using var t = new TestDb();
        var me = Guid.NewGuid();
        await Svc(t.Db).ReplaceAsync(me, JobCategory.BE, TwoCriteria());
        Assert.Equal(me, await B2CRubricScope.ResolveOwnerAsync(t.Db, me, JobCategory.BE, "vi"));
    }

    [Fact]
    public async Task ResolveOwner_CustomOnlyForOtherCategory_ReturnsNull()
    {
        using var t = new TestDb();
        var me = Guid.NewGuid();
        await Svc(t.Db).ReplaceAsync(me, JobCategory.FE, TwoCriteria());   // FE, không phải BE
        Assert.Null(await B2CRubricScope.ResolveOwnerAsync(t.Db, me, JobCategory.BE, "vi"));
    }

    // ── Publish (site 1) — regression + custom ────────────────────────────────

    [Fact]
    public async Task Publish_WithCustomRubric_UsesCandidateCriteria_NotSeed()
    {
        using var t = new TestDb();
        await SeedDefaultAsync(t.Db, JobCategory.BE);
        var me = Guid.NewGuid();
        var custom = await Svc(t.Db).ReplaceAsync(me, JobCategory.BE, TwoCriteria());
        var customIds = custom.Criteria.Select(c => c.Id).ToHashSet();

        var published = await UploadAndCaptureAsync(t, me, JobCategory.BE);

        Assert.NotNull(published);
        var publishedIds = published!.Criteria.Select(c => c.CriterionId).ToHashSet();
        Assert.Equal(customIds, publishedIds);   // chấm theo rubric RIÊNG, không phải seed
    }

    [Fact]
    public async Task Publish_NoCustomRubric_UsesSeedDefault_Regression()
    {
        using var t = new TestDb();
        await SeedDefaultAsync(t.Db, JobCategory.BE);
        var me = Guid.NewGuid();
        var seedIds = await t.Db.RubricCriteria.AsNoTracking()
            .Where(c => c.CandidateId == null && c.JobCategory == JobCategory.BE && c.IsActive)
            .Select(c => c.Id).ToListAsync();

        var published = await UploadAndCaptureAsync(t, me, JobCategory.BE);

        Assert.NotNull(published);
        Assert.Equal(seedIds.ToHashSet(), published!.Criteria.Select(c => c.CriterionId).ToHashSet());
    }

    // ── FK-safe: sửa rubric sau khi đã có answer_scores trỏ vào ───────────────

    [Fact]
    public async Task Replace_AfterAnswerScoredAgainstCriterion_IsFkSafe()
    {
        using var t = new TestDb();
        var me = Guid.NewGuid();
        var v1 = await Svc(t.Db).ReplaceAsync(me, JobCategory.BE, TwoCriteria("v1-A", "v1-B"));

        // 1 answer_scores trỏ tiêu chí v1 (FK Restrict → không được hard-delete).
        var session = TestDb.Session(me, SessionStatus.Scored, JobCategory.BE);
        var q = TestDb.Question(session.Id);
        var ans = TestDb.Answer(session.Id, q.Id, AnswerStatus.Scored, DateTime.UtcNow, DateTime.UtcNow);
        t.Db.AddRange(session, q, ans);
        t.Db.AnswerScores.Add(new AnswerScore
        {
            Id = Guid.NewGuid(), AnswerId = ans.Id, CriterionId = v1.Criteria[0].Id,
            AttemptNo = 1, Score = 4m, Reasoning = "x", RubricVersion = 1, CreatedAt = DateTime.UtcNow
        });
        await t.Db.SaveChangesAsync();

        // Sửa lại rubric — soft-deactivate v1 (không hard-delete) → KHÔNG vỡ FK.
        var ex = await Record.ExceptionAsync(
            () => Svc(t.Db).ReplaceAsync(me, JobCategory.BE, TwoCriteria("v2-A", "v2-B")));
        Assert.Null(ex);
        // answer_scores cũ vẫn trỏ tiêu chí v1 (inactive) — nguyên vẹn.
        Assert.Equal(1, await t.Db.AnswerScores.AsNoTracking().CountAsync(s => s.CriterionId == v1.Criteria[0].Id));
    }

    // ── Breakdown BC9 (site 4) dùng đúng rubric riêng ─────────────────────────

    [Fact]
    public async Task SessionResult_WithCustomRubric_BreakdownUsesCandidateCriteria()
    {
        using var t = new TestDb();
        await SeedDefaultAsync(t.Db, JobCategory.BE);
        var me = Guid.NewGuid();
        var custom = await Svc(t.Db).ReplaceAsync(me, JobCategory.BE, TwoCriteria());
        var customIds = custom.Criteria.Select(c => c.Id).ToHashSet();

        var session = TestDb.Session(me, SessionStatus.Scored, JobCategory.BE);
        var q = TestDb.Question(session.Id);
        var ans = TestDb.Answer(session.Id, q.Id, AnswerStatus.Scored, DateTime.UtcNow, DateTime.UtcNow);
        t.Db.AddRange(session, q, ans);
        foreach (var c in custom.Criteria)
            t.Db.AnswerScores.Add(new AnswerScore
            {
                Id = Guid.NewGuid(), AnswerId = ans.Id, CriterionId = c.Id,
                AttemptNo = 1, Score = 4m, Reasoning = "x", RubricVersion = 1, CreatedAt = DateTime.UtcNow
            });
        await t.Db.SaveChangesAsync();

        await TestDb.ResultService(t.Db).ComputeAndStoreAsync(session.Id);

        var rows = await t.Db.SessionCriterionScores.AsNoTracking()
            .Where(x => x.SessionId == session.Id).ToListAsync();
        Assert.Equal(customIds, rows.Select(r => r.CriterionId).ToHashSet());   // breakdown theo tiêu chí RIÊNG
    }

    // ── Helper: upload answer B2C và bắt ScoringJob đã publish (mẫu B2CRubricSeedTests) ──
    private static async Task<ScoringJob?> UploadAndCaptureAsync(TestDb t, Guid candidate, JobCategory cat)
    {
        var session = TestDb.Session(candidate, SessionStatus.Ready, cat);
        var q = TestDb.Question(session.Id);
        t.Db.AddRange(session, q);
        await t.Db.SaveChangesAsync();

        var publisher = new Mock<IScoringJobPublisher>();
        ScoringJob? published = null;
        publisher.Setup(p => p.PublishAsync(It.IsAny<ScoringJob>(), It.IsAny<CancellationToken>()))
            .Callback<ScoringJob, CancellationToken>((j, _) => published = j)
            .Returns(Task.CompletedTask);

        var storage = new Mock<IStorageService>();
        storage.Setup(s => s.UploadAsync(
                It.IsAny<Stream>(), It.IsAny<string>(), It.IsAny<Guid>(),
                It.IsAny<Guid>(), It.IsAny<string>(), It.IsAny<string>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync("answer-audio/x.webm");
        var notifier = new Mock<ISessionScoringNotifier>();
        notifier.Setup(n => n.NotifySessionScoredAsync(It.IsAny<Guid>(), It.IsAny<CancellationToken>()))
            .Returns(Task.CompletedTask);

        var svc = new AnswerService(
            t.Db, storage.Object, publisher.Object, notifier.Object,
            TestDb.ScoringOpts(), NullLogger<AnswerService>.Instance);

        using var audio = new MemoryStream([1, 2, 3]);
        await svc.UploadAnswerAsync(session.Id, q.Id, candidate, audio, "audio/webm", 30);
        return published;
    }
}
