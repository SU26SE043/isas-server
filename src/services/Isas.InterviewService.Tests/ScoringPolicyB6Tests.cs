using Isas.InterviewService.DTOs;
using Isas.InterviewService.Entities;
using Isas.InterviewService.Enums;
using Xunit;

namespace Isas.InterviewService.Tests;

/// <summary>
/// SCP1-B6 — SessionScoringNotifier dùng BIỂU THỨC ĐÃ GHIM (B5) để tính điểm buổi thi.
/// LÙI AN TOÀN (chia 0 / tràn số / ném / ngoài [0,100]) ⇒ công thức mặc định + cờ scoreFallback.
/// BÁO LỖI (total_questions = 0) ⇒ ném, KHÔNG bịa điểm. weighted_avg_pct giữ NGUYÊN (biến append-only).
/// </summary>
public class ScoringPolicyB6Tests
{
    // 1 tiêu chí weight 1.0 maxScore 5, answer scored `score`/5 ⇒ weighted_avg_pct = score/5*100.
    // `questions` = tổng số câu buổi (câu[0] được trả lời, phần còn lại để trống ⇒ answered = 1).
    private static (TestDb T, Guid SessionId) Seed(
        string? expr, int questions = 1, decimal score = 4m)
    {
        var t = new TestDb();
        var campaignId = Guid.NewGuid();
        var c = TestDb.Criterion(JobCategory.BE, version: 1, campaignId: campaignId, name: "Communication");
        t.Db.RubricCriteria.Add(c);

        var session = TestDb.Session(Guid.NewGuid(), SessionStatus.Scored, campaignId: campaignId);
        session.CampaignRubricVersion = 1;
        session.CampaignPolicyExpression = expr;
        session.CampaignPolicyVersion = expr is null ? null : 7;
        session.CampaignPolicyEngineVersion = expr is null ? null : "1";
        session.CampaignPolicyPassScorePct = expr is null ? null : 55;
        t.Db.Add(session);

        PracticeQuestion answeredQuestion;
        if (questions == 0)
        {
            // total_questions = 0 CHO BUỔI NÀY: answer buộc phải trỏ 1 câu hỏi thật (FK) ⇒ đặt câu ở
            // một session khác. Dựng đúng trạng thái "bất biến bị vi phạm" (không tồn tại ở prod).
            var zSession = TestDb.Session(Guid.NewGuid(), SessionStatus.Scored);
            t.Db.Add(zSession);
            answeredQuestion = TestDb.Question(zSession.Id, 1);
            t.Db.Add(answeredQuestion);
        }
        else
        {
            answeredQuestion = TestDb.Question(session.Id, 1);
            t.Db.Add(answeredQuestion);
            for (var i = 1; i < questions; i++) t.Db.Add(TestDb.Question(session.Id, i + 1));
        }

        var a = TestDb.Answer(session.Id, answeredQuestion.Id, AnswerStatus.Scored, DateTime.UtcNow, DateTime.UtcNow);
        t.Db.Add(a);
        t.Db.AnswerScores.Add(new AnswerScore
        {
            AnswerId = a.Id, CriterionId = c.Id, Score = score, RubricVersion = 1
        });
        t.Db.SaveChanges();
        return (t, session.Id);
    }

    private static async Task<SessionScoredEvent> Score(TestDb t, Guid sessionId)
    {
        await TestDb.Notifier(t.Db).EnqueueSessionScoredAsync(sessionId);
        await t.Db.SaveChangesAsync();
        return TestDb.ScoredOutbox(t.NewContext(), sessionId)!;
    }

    // ── (test brief) weighted_avg_pct * completeness — buổi 1/10 = 1/10 buổi 10/10 cùng chất lượng ──
    [Fact]
    public async Task Completeness_giam_diem_theo_ty_le_cau_tra_loi()
    {
        var (tFull, sFull) = Seed("weighted_avg_pct * completeness", questions: 1);   // completeness = 1/1
        using var _f = tFull;
        var (tPart, sPart) = Seed("weighted_avg_pct * completeness", questions: 10);  // completeness = 1/10
        using var _p = tPart;

        var full = await Score(tFull, sFull);
        var part = await Score(tPart, sPart);

        Assert.False(full.ScoreFallback);
        Assert.False(part.ScoreFallback);
        Assert.Equal(80m, full.TotalScore);              // 80 * 1.0
        Assert.Equal(8m, part.TotalScore);               // 80 * 0.1
        Assert.Equal(full.TotalScore, part.TotalScore * 10);
    }

    // ── (test brief) 100 / 0 → lùi an toàn, cờ bật, KHÔNG ném ──────────────────────────────────
    [Fact]
    public async Task Chia0_LuiAnToan_CoBat_KhongNem()
    {
        var (t, s) = Seed("100 / 0", questions: 1);
        using var _ = t;

        var evt = await Score(t, s);   // KHÔNG ném

        Assert.True(evt.ScoreFallback);
        Assert.Equal(80m, evt.TotalScore);   // = công thức mặc định (weighted), KHÔNG phải điểm bịa
    }

    // ── (test brief) total_questions = 0 → BÁO LỖI ĐÁNH GIÁ, KHÔNG bịa điểm ────────────────────
    [Fact]
    public async Task TotalQuestions0_BaoLoi_KhongPhatEvent()
    {
        var (t, s) = Seed("weighted_avg_pct", questions: 0);
        using var _ = t;

        await Assert.ThrowsAsync<InvalidOperationException>(
            () => TestDb.Notifier(t.Db).EnqueueSessionScoredAsync(s));

        await t.Db.SaveChangesAsync();
        Assert.Null(TestDb.ScoredOutbox(t.NewContext(), s));   // KHÔNG có event điểm bịa
    }

    // ── ngoài [0,100] → lùi an toàn, KHÔNG clamp ─────────────────────────────────────────────
    [Fact]
    public async Task KetQuaNgoaiDai_LuiAnToan_KhongClampVe100()
    {
        var (t, s) = Seed("weighted_avg_pct + 100", questions: 1);   // 80 + 100 = 180
        using var _ = t;

        var evt = await Score(t, s);

        Assert.True(evt.ScoreFallback);
        Assert.Equal(80m, evt.TotalScore);   // lùi về mặc định — KHÔNG phải 100 (clamp che lỗi policy)
    }

    // ── biểu thức hợp lệ → THAY điểm mặc định ─────────────────────────────────────────────────
    [Fact]
    public async Task BieuThucHopLe_ThayDiemMacDinh_KhongBatCo()
    {
        var (t, s) = Seed("min(weighted_avg_pct, 50)", questions: 1);   // min(80, 50) = 50
        using var _ = t;

        var evt = await Score(t, s);

        Assert.False(evt.ScoreFallback);
        Assert.Equal(50m, evt.TotalScore);
    }

    // ── CẤM #1 — weighted_avg_pct là BIẾN, không đổi định nghĩa: dùng thẳng = y hệt mặc định ───
    [Fact]
    public async Task weighted_avg_pct_LaBien_GiuNguyenGiaTri()
    {
        var (t, s) = Seed("weighted_avg_pct", questions: 1);
        using var _ = t;

        var evt = await Score(t, s);

        Assert.False(evt.ScoreFallback);
        Assert.Equal(80m, evt.TotalScore);   // = công thức weighted mặc định
    }

    // ── (5) buổi CHƯA ghim policy (B2C / trước SCP1) → công thức mặc định, cờ tắt ─────────────
    [Fact]
    public async Task ChuaGhimPolicy_DungCongThucMacDinh()
    {
        var (t, s) = Seed(expr: null, questions: 1);
        using var _ = t;

        var evt = await Score(t, s);

        Assert.False(evt.ScoreFallback);
        Assert.Equal(80m, evt.TotalScore);
    }
}
