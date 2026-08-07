using Isas.InterviewService.Entities;
using Isas.InterviewService.Enums;
using Microsoft.EntityFrameworkCore;

namespace Isas.InterviewService.Tests;

// DB30 — điểm tổng (weighted) trong SessionScoringNotifier phải lấy tiêu chí B2C qua
// B2CRubricScope.ResolveOwnerAsync như 5 call-site kia, không phải "mọi rubric cùng nghề".
public class WeightedTotalRubricScopeDb30Tests
{
    // Seed 1 answer đã chấm: score/maxScore cho trước để suy ra điểm tổng kỳ vọng.
    private static PracticeAnswer SeedScoredAnswer(
        TestDb t, PracticeSession session, params (RubricCriterion Crit, decimal Score)[] scores)
    {
        var q = TestDb.Question(session.Id);
        var a = TestDb.Answer(session.Id, q.Id, AnswerStatus.Scored,
            DateTime.UtcNow.AddMinutes(-5), lastPublished: DateTime.UtcNow.AddMinutes(-4));
        t.Db.AddRange(q, a);
        foreach (var (crit, score) in scores)
            t.Db.Add(new AnswerScore
            {
                AnswerId = a.Id, CriterionId = crit.Id, Score = score, RubricVersion = crit.Version
            });
        return a;
    }

    private static async Task<decimal> ScoredTotal(TestDb t, PracticeSession session)
    {
        await TestDb.Notifier(t.Db).EnqueueSessionScoredAsync(session.Id);
        await t.Db.SaveChangesAsync();
        return TestDb.ScoredOutbox(t.NewContext(), session.Id)!.TotalScore;
    }

    // Rubric của candidate đổi giữa chừng (BC16 soft-version): answer_scores còn giữ điểm gắn với tiêu
    // chí SEED cũ, trong khi rubric đang hiệu lực là bản RIÊNG. Điểm tổng phải theo rubric đang hiệu lực.
    // Không scope theo candidate_id → seed lọt vào weightSum → 60 thay vì 100 → ĐỎ.
    [Fact]
    public async Task Db30_B2C_WithCustomRubric_IgnoresStaleSeedScores()
    {
        using var t = new TestDb();
        var candidate = Guid.NewGuid();
        var seed = TestDb.Criterion(JobCategory.BE, name: "Seed-Crit");
        var custom = TestDb.Criterion(JobCategory.BE, name: "Custom-Crit", candidateId: candidate);
        var session = TestDb.Session(candidate, SessionStatus.Scored, JobCategory.BE);
        t.Db.AddRange(seed, custom, session);
        SeedScoredAnswer(t, session, (custom, 5m), (seed, 1m));   // 100% vs 20%, cùng weight 1.0
        await t.Db.SaveChangesAsync();

        Assert.Equal(100m, await ScoredTotal(t, session));
    }

    // Rubric RIÊNG của candidate KHÁC (cùng nghề) không được kéo vào phép tính của người này.
    [Fact]
    public async Task Db30_B2C_OtherCandidatesCustomRubric_NotCounted()
    {
        using var t = new TestDb();
        var me = Guid.NewGuid();
        var other = Guid.NewGuid();
        var seed = TestDb.Criterion(JobCategory.BE, name: "Seed-Crit");
        var otherCustom = TestDb.Criterion(JobCategory.BE, name: "Other-Crit", candidateId: other);
        var session = TestDb.Session(me, SessionStatus.Scored, JobCategory.BE);
        t.Db.AddRange(seed, otherCustom, session);
        SeedScoredAnswer(t, session, (seed, 4m), (otherCustom, 0m));   // 80% vs 0%
        await t.Db.SaveChangesAsync();

        Assert.Equal(80m, await ScoredTotal(t, session));
    }

    // REGRESSION — đường thường (candidate chưa khai rubric riêng, chấm theo seed): điểm KHÔNG đổi.
    [Fact]
    public async Task Db30_B2C_SeedOnly_TotalUnchanged()
    {
        using var t = new TestDb();
        var candidate = Guid.NewGuid();
        var seed = TestDb.Criterion(JobCategory.BE, name: "Seed-Crit");
        var session = TestDb.Session(candidate, SessionStatus.Scored, JobCategory.BE);
        t.Db.AddRange(seed, session);
        SeedScoredAnswer(t, session, (seed, 3m));   // 3/5 = 60%
        await t.Db.SaveChangesAsync();

        Assert.Equal(60m, await ScoredTotal(t, session));
    }

    // REGRESSION — B2B vẫn theo campaign (không đụng resolver B2C), điểm KHÔNG đổi.
    [Fact]
    public async Task Db30_B2B_StillScopedByCampaign()
    {
        using var t = new TestDb();
        var campaignId = Guid.NewGuid();
        var campCrit = TestDb.Criterion(JobCategory.BE, campaignId: campaignId, name: "Campaign-Crit");
        var b2cSeed = TestDb.Criterion(JobCategory.BE, name: "Seed-Crit");
        var session = TestDb.Session(Guid.NewGuid(), SessionStatus.Scored, JobCategory.BE, campaignId: campaignId);
        t.Db.AddRange(campCrit, b2cSeed, session);
        SeedScoredAnswer(t, session, (campCrit, 2m), (b2cSeed, 5m));   // 40% (campaign) vs 100% (seed)
        await t.Db.SaveChangesAsync();

        Assert.Equal(40m, await ScoredTotal(t, session));
    }

    // ── Q8: điểm tổng của event cũng phải scope theo NGÔN NGỮ ────────────────────────────────
    // Ca thật: RubricLibraryService luôn sinh rubric riêng Language="vi" (không set Language →
    // rơi về default entity). Ứng viên đó làm buổi "en":
    //   · đường CHẤM resolve theo session.Language="en" → hasOwn=false → dùng SEED en
    //   · notifier (trước vá) gọi overload hard-code "vi" → hasOwn=TRUE → dùng rubric riêng VI
    // Hai bộ tiêu chí KHÁC HẲN ⇒ giao ID rỗng ⇒ mọi vòng `continue` ⇒ weightSum=0 ⇒ TotalScore=0.
    // Không phải "lệch nhẹ" — là mất trắng điểm trong payload event.
    [Fact]
    public async Task Q8_UngVienCoRubricRiengVI_BuoiEN_KhongSapVe0()
    {
        using var t = new TestDb();
        var candidate = Guid.NewGuid();
        var seedEn = TestDb.Criterion(JobCategory.BE, name: "Technical depth", language: "en");
        // rubric riêng của chính ứng viên này nhưng Ở NGÔN NGỮ KHÁC — đúng thứ RubricLibraryService đẻ ra
        var customVi = TestDb.Criterion(JobCategory.BE, name: "Chiều sâu kỹ thuật",
            candidateId: candidate, language: "vi");
        var session = TestDb.Session(candidate, SessionStatus.Scored, JobCategory.BE, language: "en");
        t.Db.AddRange(seedEn, customVi, session);
        SeedScoredAnswer(t, session, (seedEn, 4m));   // 4/5 = 80%, weight 1.0
        await t.Db.SaveChangesAsync();

        Assert.Equal(80m, await ScoredTotal(t, session));   // trước vá: 0
    }
}
