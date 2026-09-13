using System.Text.Json;
using Isas.CampaignService.DTOs;
using Isas.CampaignService.Models;
using Isas.CampaignService.Services;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging;
using Moq;

namespace Isas.CampaignService.Tests;

/// <summary>
/// SC2 · T6 — chấm thử THEO PHẠM VI CÂU + quota theo câu (D-4).
///
/// <para><b>I6 — chấm thử = chấm thật:</b> lượt chấm thử của câu Q chấm ĐÚNG tập tiêu chí mà ứng viên trả lời
/// Q sẽ bị chấm (INT-18): <c>scope(Q) = Always ∪ {c | c.Id ∈ Q.TargetCriterionIds}</c>. <c>null</c> ⇒ TOÀN BỘ;
/// <c>[]</c> ⇒ chỉ <c>Always</c> (I2). Bộ gửi <c>/score-preview</c> vẫn dựng bằng CÙNG
/// <c>ScoringCriteriaBuilder.Build</c>, chỉ khác TẬP tiêu chí — đối chứng byte-equal cho câu không nhãn.</para>
///
/// <para><b>D-4:</b> quota free 1/(campaign, rubricVersion, questionId) — lượt của câu A không ăn quota câu B;
/// history tính <c>freeRunsRemaining</c> theo ĐÚNG (version, câu) của từng run.</para>
///
/// <para><b>I5:</b> snapshot trước T6 không có <c>InScope</c> ⇒ deserialize null ⇒ coi là toàn bộ.</para>
/// <para><b>I7:</b> mọi 400 vẫn TRƯỚC ReserveAsync (credits mock Strict — bất kỳ lời gọi nào là ném).</para>
/// </summary>
public class RubricPreviewScopeSc2Tests
{
    private const string D0 = "CÓ: không nêu được ý nào | CÒN THIẾU: mọi thứ";
    private const string DTop = "CÓ: nêu đủ ý, ví dụ, đánh đổi | CÒN THIẾU: không đáng kể";

    private static RubricPreviewService NewService(CampaignDbContext db, IRubricPreviewClient ai, ICreditReservationClient? credits = null)
        => new(db, ai, Mock.Of<ILogger<RubricPreviewService>>(), credits);

    /// <summary>AI giả: ghi lại bộ tiêu chí nhận được, trả điểm cho ĐÚNG những tiêu chí đó.</summary>
    private static (Mock<IRubricPreviewClient> mock, List<IReadOnlyList<PreviewCriterionInput>> received) AiEcho(decimal score = 3)
    {
        var received = new List<IReadOnlyList<PreviewCriterionInput>>();
        var ai = new Mock<IRubricPreviewClient>();
        ai.Setup(x => x.RunAsync(It.IsAny<string>(), It.IsAny<string>(), It.IsAny<string?>(),
                It.IsAny<string>(), It.IsAny<string?>(), It.IsAny<string?>(), It.IsAny<int>(),
                It.IsAny<IReadOnlyList<PreviewCriterionInput>>(), It.IsAny<CancellationToken>()))
            .Returns((string _, string _, string? _, string _, string? _, string? _, int _,
                    IReadOnlyList<PreviewCriterionInput> crit, CancellationToken _) =>
            {
                received.Add(crit);
                return Task.FromResult(new RubricPreviewResult(
                    new[] { "Weak", "Good", "Excellent" }.Select(b => new PreviewSample(
                        b, $"bài {b}", 160,
                        crit.Select(c => new PreviewSampleScore(c.CriterionId, score, (int)score, "vì")).ToList())).ToList(),
                    PromptVersion: 4, LengthParityWarning: false));
            });
        return (ai, received);
    }

    private static CampaignCriterionLevel Level(Guid criterionId, int score, string d)
        => new() { Id = Guid.NewGuid(), CriterionId = criterionId, Score = score, Descriptor = d, CreatedAt = DateTime.UtcNow, UpdatedAt = DateTime.UtcNow };

    private sealed record Seeded(Campaign Camp, CampaignCriterion Always, CampaignCriterion Wt1, CampaignCriterion Wt2,
        CampaignQuestion QNull, CampaignQuestion QEmpty, CampaignQuestion QWt1);

    /// <summary>
    /// 3 tiêu chí (A Always · W1/W2 WhenTargeted, đủ mốc trừ khi <paramref name="wt2Levels"/> = false) +
    /// 3 câu: null (chưa gắn) · [] (đã xét, không nhắm) · [W1].
    /// </summary>
    private static async Task<Seeded> SeedAsync(CampaignTestDb tdb, Guid owner, bool wt2Levels = true, bool withAlways = true)
    {
        var camp = CampaignTestDb.NewCampaign(owner);
        camp.Domain = "BE";
        tdb.Db.Campaigns.Add(camp);

        CampaignCriterion Crit(int order, string name, CriterionScoringScope scope, bool levels, decimal weight)
        {
            var c = new CampaignCriterion
            {
                Id = Guid.NewGuid(), CampaignId = camp.Id, OrderNo = order, Name = name, Weight = weight, MaxScore = 5,
                Source = CriterionSource.HrEdited, ScoringScope = scope, CreatedAt = DateTime.UtcNow, UpdatedAt = DateTime.UtcNow
            };
            tdb.Db.CampaignCriteria.Add(c);
            if (levels) tdb.Db.CampaignCriterionLevels.AddRange(Level(c.Id, 0, D0), Level(c.Id, 5, DTop));
            return c;
        }
        // Σw = 1 (C12); phạm vi câu nhắm W1 = Always ∪ W1 ⇒ Σw = 0.7 — đúng seed probe P1 của Tester.
        var always = Crit(0, "Cach noi", withAlways ? CriterionScoringScope.Always : CriterionScoringScope.WhenTargeted, true, 0.4m);
        var wt1 = Crit(1, "Noi dung 1", CriterionScoringScope.WhenTargeted, true, 0.3m);
        var wt2 = Crit(2, "Noi dung 2", CriterionScoringScope.WhenTargeted, wt2Levels, 0.3m);

        CampaignQuestion Q(string text, List<Guid>? targets, int i)
        {
            var q = new CampaignQuestion
            {
                Id = Guid.NewGuid(), CampaignId = camp.Id, OrgId = owner, QuestionText = text, Source = QuestionSource.CustomHr,
                TargetCriterionIds = targets, CreatedAt = new DateTime(2026, 1, 1, 0, 0, i, DateTimeKind.Utc)
            };
            tdb.Db.CampaignQuestions.Add(q);
            return q;
        }
        var qNull = Q("cau chua gan nhan", null, 0);
        var qEmpty = Q("cau xa giao", new(), 1);
        var qWt1 = Q("cau nham W1", new() { wt1.Id }, 2);

        await tdb.Db.SaveChangesAsync();
        return new Seeded(camp, always, wt1, wt2, qNull, qEmpty, qWt1);
    }

    private static RubricPreviewRequest For(CampaignQuestion q) => new() { QuestionId = q.Id };

    private static Guid[] Ids(IReadOnlyList<PreviewCriterionInput> crit) => crit.Select(c => c.CriterionId).ToArray();

    // ═══════════════ phạm vi ═══════════════

    [Fact]
    public void ScopeFor_NullToanBo_RongChiAlways_NhanThemDungId()
    {
        var a = new CampaignCriterion { Id = Guid.NewGuid(), ScoringScope = CriterionScoringScope.Always };
        var w1 = new CampaignCriterion { Id = Guid.NewGuid(), ScoringScope = CriterionScoringScope.WhenTargeted };
        var w2 = new CampaignCriterion { Id = Guid.NewGuid(), ScoringScope = CriterionScoringScope.WhenTargeted };
        var all = new[] { a, w1, w2 };

        Assert.Equal(new[] { a.Id, w1.Id, w2.Id }.ToHashSet(), RubricPreviewService.ScopeFor(all, null));
        Assert.Equal(new[] { a.Id }.ToHashSet(), RubricPreviewService.ScopeFor(all, Array.Empty<Guid>()));   // [] ≠ null
        Assert.Equal(new[] { a.Id, w2.Id }.ToHashSet(), RubricPreviewService.ScopeFor(all, new[] { w2.Id, Guid.NewGuid() }));   // id lạ không thêm gì
    }

    [Fact]
    public async Task CauChuaGanNhan_ChamToanBo()
    {
        using var tdb = new CampaignTestDb();
        var owner = Guid.NewGuid();
        var s = await SeedAsync(tdb, owner);
        var (ai, received) = AiEcho();

        var res = await NewService(tdb.NewContext(), ai.Object, new Mock<ICreditReservationClient>(MockBehavior.Strict).Object)
            .RunAsync(owner, owner, s.Camp.Id, For(s.QNull), default);

        var all = new[] { s.Always.Id, s.Wt1.Id, s.Wt2.Id };
        Assert.Equal(all, Ids(received.Single()));
        Assert.Equal(all, res.ScopedCriterionIds);
        Assert.Equal(3, res.Rubric.Count);
        Assert.All(res.Rubric, c => Assert.True(c.InScope));
        Assert.All(res.Samples, sm => Assert.Equal(all, sm.Scores.Select(x => x.CriterionId)));
    }

    [Fact]
    public async Task CauXaGiao_NhanRong_ChiChamAlways()
    {
        using var tdb = new CampaignTestDb();
        var owner = Guid.NewGuid();
        var s = await SeedAsync(tdb, owner);
        var (ai, received) = AiEcho();

        var res = await NewService(tdb.NewContext(), ai.Object, new Mock<ICreditReservationClient>(MockBehavior.Strict).Object)
            .RunAsync(owner, owner, s.Camp.Id, For(s.QEmpty), default);

        Assert.Equal(new[] { s.Always.Id }, Ids(received.Single()));
        Assert.Equal(new[] { s.Always.Id }, res.ScopedCriterionIds);
        // Rubric vẫn ĐỦ 3 (HR thấy cả thước đo), chỉ khác InScope
        Assert.Equal(3, res.Rubric.Count);
        Assert.Equal(new[] { true, false, false }, res.Rubric.Select(c => c.InScope!.Value));
        Assert.Equal(new[] { "Always", "WhenTargeted", "WhenTargeted" }, res.Rubric.Select(c => c.ScoringScope));
        Assert.All(res.Samples, sm => Assert.Equal(new[] { s.Always.Id }, sm.Scores.Select(x => x.CriterionId)));
    }

    [Fact]
    public async Task CauNhamW1_ChamAlwaysVaW1_KhongW2()
    {
        using var tdb = new CampaignTestDb();
        var owner = Guid.NewGuid();
        var s = await SeedAsync(tdb, owner);
        var (ai, received) = AiEcho();

        var res = await NewService(tdb.NewContext(), ai.Object, new Mock<ICreditReservationClient>(MockBehavior.Strict).Object)
            .RunAsync(owner, owner, s.Camp.Id, For(s.QWt1), default);

        Assert.Equal(new[] { s.Always.Id, s.Wt1.Id }, Ids(received.Single()));   // thứ tự OrderNo giữ nguyên
        Assert.Equal(new[] { s.Always.Id, s.Wt1.Id }, res.ScopedCriterionIds);
        Assert.DoesNotContain(s.Wt2.Id, res.Samples[0].Scores.Select(x => x.CriterionId));
    }

    /// <summary>I6 đối chứng: bộ gửi AI cho câu KHÔNG nhãn == đúng đầu ra BuildPreviewCriteria(toàn bộ) như trước T6.</summary>
    [Fact]
    public async Task ByteEqual_CauKhongNhan_PayloadNhuTruocT6()
    {
        using var tdb = new CampaignTestDb();
        var owner = Guid.NewGuid();
        var s = await SeedAsync(tdb, owner);
        var (ai, received) = AiEcho();

        await NewService(tdb.NewContext(), ai.Object).RunAsync(owner, owner, s.Camp.Id, For(s.QNull), default);

        using var db = tdb.NewContext();
        var all = await db.CampaignCriteria.Include(c => c.Levels).Where(c => c.CampaignId == s.Camp.Id).OrderBy(c => c.OrderNo).ToListAsync();
        var json = new JsonSerializerOptions(JsonSerializerDefaults.Web);
        Assert.Equal(
            JsonSerializer.Serialize(RubricPreviewService.BuildPreviewCriteria(all), json),   // đường TRƯỚC T6
            JsonSerializer.Serialize(received.Single(), json));
    }

    // ═══════════════ guard mốc theo phạm vi (I7: trước Reserve) ═══════════════

    [Fact]
    public async Task MocThieu_NgoaiPhamVi_KhongChan_TrongPhamVi_400_TruocReserve()
    {
        using var tdb = new CampaignTestDb();
        var owner = Guid.NewGuid();
        var s = await SeedAsync(tdb, owner, wt2Levels: false);   // W2 KHÔNG có mốc
        var credits = new Mock<ICreditReservationClient>(MockBehavior.Strict);
        var (ai, _) = AiEcho();

        // câu nhắm W1: W2 ngoài phạm vi ⇒ không chặn
        var ok = await NewService(tdb.NewContext(), ai.Object, credits.Object).RunAsync(owner, owner, s.Camp.Id, For(s.QWt1), default);
        Assert.Equal("Succeeded", ok.Status);

        // Correction T6 (Tester P4): quota câu QNull ĐÃ HẾT ⇒ nếu guard mốc đứng sau bước quota thì lượt
        // này billed=true THẬT và chạm ReserveAsync ⇒ Strict credits ném. Ở billed=false test là vacuous.
        using (var db = tdb.NewContext())
        {
            db.RubricPreviewRuns.Add(new RubricPreviewRun
            {
                Id = Guid.NewGuid(), CampaignId = s.Camp.Id, CreatedByUserId = owner, QuestionId = s.QNull.Id,
                QuestionText = "q", Status = RubricPreviewStatus.Succeeded, RubricSnapshot = "[]", RubricFingerprint = "fp",
                RubricVersion = 1, CreatedAt = DateTime.UtcNow.AddSeconds(-10),
            });
            await db.SaveChangesAsync();
        }

        // câu chưa gắn nhãn: toàn bộ ⇒ W2 trong phạm vi ⇒ 400 nêu tên, KHÔNG chạm Payment, không để row Running
        var ex = await Assert.ThrowsAsync<ArgumentException>(() =>
            NewService(tdb.NewContext(), ai.Object, credits.Object).RunAsync(owner, owner, s.Camp.Id, For(s.QNull), default));
        Assert.Contains("Noi dung 2", ex.Message);
        credits.VerifyNoOtherCalls();
        using var check = tdb.NewContext();
        Assert.Equal(2, await check.RubricPreviewRuns.CountAsync(r => r.CampaignId == s.Camp.Id));   // ok + seed, không row nửa vời
    }

    [Fact]
    public async Task CauNhanRong_KhongCoAlways_400_KhongChamPayment()
    {
        using var tdb = new CampaignTestDb();
        var owner = Guid.NewGuid();
        var s = await SeedAsync(tdb, owner, withAlways: false);   // 0 tiêu chí Always
        var credits = new Mock<ICreditReservationClient>(MockBehavior.Strict);
        var (ai, received) = AiEcho();

        var ex = await Assert.ThrowsAsync<ArgumentException>(() =>
            NewService(tdb.NewContext(), ai.Object, credits.Object).RunAsync(owner, owner, s.Camp.Id, For(s.QEmpty), default));

        Assert.Contains("Always", ex.Message);
        Assert.Empty(received);
        credits.VerifyNoOtherCalls();
    }

    // ═══════════════ quota theo câu (D-4) ═══════════════

    [Fact]
    public async Task Quota_CauA_LuotHai_Billed_CauB_LuotDau_Free_CungVersion()
    {
        using var tdb = new CampaignTestDb();
        var owner = Guid.NewGuid();
        var s = await SeedAsync(tdb, owner);
        var (ai, _) = AiEcho();
        var credits = new Mock<ICreditReservationClient>();
        credits.Setup(x => x.ReserveAsync("Org", owner, It.IsAny<Guid>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(new CreditReservationResult(Guid.NewGuid(), 1));

        var a1 = await NewService(tdb.NewContext(), ai.Object, credits.Object).RunAsync(owner, owner, s.Camp.Id, For(s.QWt1), default);
        var a2 = await NewService(tdb.NewContext(), ai.Object, credits.Object).RunAsync(owner, owner, s.Camp.Id, For(s.QWt1), default);
        var b1 = await NewService(tdb.NewContext(), ai.Object, credits.Object).RunAsync(owner, owner, s.Camp.Id, For(s.QEmpty), default);

        Assert.False(a1.Billed); Assert.Equal(0, a1.FreeRunsRemaining);
        Assert.True(a2.Billed);  Assert.Equal(0, a2.FreeRunsRemaining);
        Assert.False(b1.Billed); Assert.Equal(0, b1.FreeRunsRemaining);   // câu B không ăn quota câu A
        credits.Verify(x => x.ReserveAsync("Org", owner, a2.Id, It.IsAny<CancellationToken>()), Times.Once);
        credits.Verify(x => x.ReserveAsync(It.IsAny<string>(), It.IsAny<Guid>(), It.IsAny<Guid>(), It.IsAny<CancellationToken>()), Times.Once);
    }

    [Fact]
    public async Task Quota_DoiVersion_CauDoFreeLai()
    {
        using var tdb = new CampaignTestDb();
        var owner = Guid.NewGuid();
        var s = await SeedAsync(tdb, owner);
        var (ai, _) = AiEcho();
        var credits = new Mock<ICreditReservationClient>(MockBehavior.Strict);

        var v1 = await NewService(tdb.NewContext(), ai.Object, credits.Object).RunAsync(owner, owner, s.Camp.Id, For(s.QWt1), default);
        Assert.False(v1.Billed);

        using (var db = tdb.NewContext())
        {
            var camp = await db.Campaigns.SingleAsync(c => c.Id == s.Camp.Id);
            camp.RubricVersion = 2;
            await db.SaveChangesAsync();
        }

        var v2 = await NewService(tdb.NewContext(), ai.Object, credits.Object).RunAsync(owner, owner, s.Camp.Id, For(s.QWt1), default);
        Assert.False(v2.Billed);
        Assert.Equal(2, v2.RubricVersion);
        credits.VerifyNoOtherCalls();
    }

    [Fact]
    public async Task History_FreeRunsRemaining_TheoDung_VersionVaCau_CuaTungRun()
    {
        using var tdb = new CampaignTestDb();
        var owner = Guid.NewGuid();
        var s = await SeedAsync(tdb, owner);
        var (ai, _) = AiEcho();
        var credits = new Mock<ICreditReservationClient>();
        credits.Setup(x => x.ReserveAsync(It.IsAny<string>(), It.IsAny<Guid>(), It.IsAny<Guid>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(new CreditReservationResult(Guid.NewGuid(), 1));

        // v1: câu W1 ×2 (free rồi billed) · câu rỗng ×1 (free); bump v2: câu W1 ×1 (free lại)
        var svc = () => NewService(tdb.NewContext(), ai.Object, credits.Object);
        await svc().RunAsync(owner, owner, s.Camp.Id, For(s.QWt1), default);
        await svc().RunAsync(owner, owner, s.Camp.Id, For(s.QWt1), default);
        await svc().RunAsync(owner, owner, s.Camp.Id, For(s.QEmpty), default);
        using (var db = tdb.NewContext())
        {
            (await db.Campaigns.SingleAsync(c => c.Id == s.Camp.Id)).RubricVersion = 2;
            await db.SaveChangesAsync();
        }
        await svc().RunAsync(owner, owner, s.Camp.Id, For(s.QWt1), default);

        var history = await NewService(tdb.NewContext(), ai.Object).GetHistoryAsync(owner, s.Camp.Id, default);

        Assert.Equal(4, history.Count);
        // (v1, W1) đã có 2 Succeeded ⇒ 0 · (v1, rỗng) 1 Succeeded ⇒ 0 · (v2, W1) 1 Succeeded ⇒ 0 — mọi run 0 free,
        // NHƯNG phải tính theo đúng cặp: seed thêm một run Failed của câu W1 ở v3 (chưa Succeeded) ⇒ 1.
        Assert.All(history, r => Assert.Equal(0, r.FreeRunsRemaining));

        using (var db = tdb.NewContext())
        {
            db.RubricPreviewRuns.Add(new RubricPreviewRun
            {
                Id = Guid.NewGuid(), CampaignId = s.Camp.Id, CreatedByUserId = owner, QuestionId = s.QWt1.Id,
                QuestionText = "q", Status = RubricPreviewStatus.Failed, RubricSnapshot = "[]", RubricFingerprint = "fp",
                RubricVersion = 3, CreatedAt = DateTime.UtcNow.AddSeconds(5),
            });
            await db.SaveChangesAsync();
        }
        var again = await NewService(tdb.NewContext(), ai.Object).GetHistoryAsync(owner, s.Camp.Id, default);
        Assert.Equal(5, again.Count);
        Assert.Equal(RubricPreviewService.FreeRunsPerQuestion, again[0].FreeRunsRemaining);   // (v3, W1): chưa Succeeded ⇒ còn 1
        Assert.All(again.Skip(1), r => Assert.Equal(0, r.FreeRunsRemaining));                 // các cặp khác vẫn 0 — không dùng chung runs[0]
        Assert.All(again, r => Assert.NotNull(r.QuestionId));
    }

    // ═══════════════ correction T6 — % tổng chia Σw của PHẠM VI (mirror SessionScoringNotifier) ═══════════════

    /// <summary>
    /// Tester probe P1: Always 0.4 · W1 0.3 · W2 0.3, AI 5/5 mọi tiêu chí. Câu nhắm W1 ⇒ phạm vi Σw = 0.7.
    /// Đường chấm thật chia Σ(pct×w)/Σw (chỉ tiêu chí có điểm) ⇒ 100; thiếu phép chia ⇒ 70 ⇒ FE so ngưỡng
    /// tuyệt đối ⇒ verdict oan. Kỳ vọng cũng chia Σw ⇒ = pct của mức kỳ vọng (hai tiêu chí cùng mốc).
    /// </summary>
    [Fact]
    public async Task ScopedSumWeight07_AI5tren5_ActualWeightedPct_La100_KhongPhai70()
    {
        using var tdb = new CampaignTestDb();
        var owner = Guid.NewGuid();
        var s = await SeedAsync(tdb, owner);
        var (ai, _) = AiEcho(score: 5);

        var scoped = await NewService(tdb.NewContext(), ai.Object).RunAsync(owner, owner, s.Camp.Id, For(s.QWt1), default);
        var full = await NewService(tdb.NewContext(), ai.Object).RunAsync(owner, owner, s.Camp.Id, For(s.QNull), default);

        var excellent = scoped.Samples.Single(x => x.Band == "Excellent");
        Assert.Equal(100m, excellent.ActualWeightedPct);                                   // KHÔNG phải 70
        Assert.Equal(full.Samples.Single(x => x.Band == "Excellent").ActualWeightedPct, excellent.ActualWeightedPct);

        // Kỳ vọng: mọi tiêu chí trong phạm vi cùng mốc {0,5} ⇒ % tổng == % của mức kỳ vọng, bất kể Σw.
        var (weak, good, exc) = RubricPreviewService.ExpectedLevels(
            new List<CampaignCriterionLevel> { Level(s.Wt1.Id, 0, D0), Level(s.Wt1.Id, 5, DTop) });
        Assert.Equal(Math.Round(exc / 5m * 100m, 2), excellent.ExpectedWeightedPct);
        Assert.Equal(Math.Round(weak / 5m * 100m, 2), scoped.Samples.Single(x => x.Band == "Weak").ExpectedWeightedPct);
        Assert.Equal(Math.Round(good / 5m * 100m, 2), scoped.Samples.Single(x => x.Band == "Good").ExpectedWeightedPct);
        // Toàn bộ (Σw = 1) ⇒ số y như trước correction.
        Assert.Equal(excellent.ExpectedWeightedPct, full.Samples.Single(x => x.Band == "Excellent").ExpectedWeightedPct);
    }

    /// <summary>
    /// Test-gap 1: cùng version, câu A `Succeeded`, câu B chỉ `Failed` ⇒ A còn 0, B còn 1. GroupBy chỉ theo
    /// RubricVersion (gộp hai câu) sẽ cho B = 0 ⇒ ĐỎ.
    /// </summary>
    [Fact]
    public async Task History_CungVersion_CauA_Succeeded_CauB_ChiFailed_FreeTheoTungCau()
    {
        using var tdb = new CampaignTestDb();
        var owner = Guid.NewGuid();
        var s = await SeedAsync(tdb, owner);
        RubricPreviewRun Run(Guid qid, RubricPreviewStatus st, int secs) => new()
        {
            Id = Guid.NewGuid(), CampaignId = s.Camp.Id, CreatedByUserId = owner, QuestionId = qid, QuestionText = "q",
            Status = st, RubricSnapshot = "[]", RubricFingerprint = "fp", RubricVersion = 1,
            CreatedAt = DateTime.UtcNow.AddSeconds(secs),
        };
        var a = Run(s.QWt1.Id, RubricPreviewStatus.Succeeded, 0);
        var b = Run(s.QEmpty.Id, RubricPreviewStatus.Failed, 1);
        tdb.Db.RubricPreviewRuns.AddRange(a, b);
        await tdb.Db.SaveChangesAsync();

        var history = await NewService(tdb.NewContext(), Mock.Of<IRubricPreviewClient>()).GetHistoryAsync(owner, s.Camp.Id, default);

        Assert.Equal(0, history.Single(r => r.Id == a.Id).FreeRunsRemaining);
        Assert.Equal(RubricPreviewService.FreeRunsPerQuestion, history.Single(r => r.Id == b.Id).FreeRunsRemaining);
    }

    // ═══════════════ I5 — row cũ trước T6 ═══════════════

    [Fact]
    public async Task RowCu_SnapshotKhongCoInScope_ScopedCriterionIds_LaTatCa()
    {
        using var tdb = new CampaignTestDb();
        var owner = Guid.NewGuid();
        var s = await SeedAsync(tdb, owner);
        var a = Guid.NewGuid(); var b = Guid.NewGuid();
        // Snapshot đúng hình dạng TRƯỚC T6: không có scoringScope/inScope.
        var oldSnapshot = $$"""
            [{"criterionId":"{{a}}","name":"A","weight":0.5,"maxScore":5,"levels":[]},
             {"criterionId":"{{b}}","name":"B","weight":0.5,"maxScore":5,"levels":[]}]
            """;
        tdb.Db.RubricPreviewRuns.Add(new RubricPreviewRun
        {
            Id = Guid.NewGuid(), CampaignId = s.Camp.Id, CreatedByUserId = owner, QuestionId = s.QNull.Id,
            QuestionText = "q", Status = RubricPreviewStatus.Succeeded, RubricSnapshot = oldSnapshot, RubricFingerprint = "fp",
            RubricVersion = 1, CreatedAt = DateTime.UtcNow,
        });
        await tdb.Db.SaveChangesAsync();

        var history = await NewService(tdb.NewContext(), Mock.Of<IRubricPreviewClient>()).GetHistoryAsync(owner, s.Camp.Id, default);

        var run = Assert.Single(history);
        Assert.Equal(new[] { a, b }, run.ScopedCriterionIds);
        Assert.All(run.Rubric, c => { Assert.Null(c.InScope); Assert.Null(c.ScoringScope); });
    }
}
