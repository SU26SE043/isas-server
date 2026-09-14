using Isas.InterviewService.Entities;
using Isas.InterviewService.Enums;
using Isas.InterviewService.Services;
using Isas.InterviewService.Services.Interfaces;
using Isas.Shared.Scoring;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging.Abstractions;
using Moq;
using Xunit;

namespace Isas.InterviewService.Tests;

/// <summary>
/// CAMP-21 (2026-09-11) — bài <c>Skipped</c> do VAD xác nhận IM LẶNG (<c>reject_reason = 'no_speech'</c>)
/// KHÔNG còn tính là "đã trả lời" khi tính <c>answered</c>/<c>seed_answered</c>. Hai nghĩa còn lại của
/// <c>Skipped</c> giữ nguyên: buổi kẹt bị chốt sổ (có audio, reason null) VẪN tính; câu chưa từng ghi âm
/// (không audio) vốn đã không tính.
///
/// <para>Bằng chứng đo trên dev: trả lời 1/3 câu + 1 bài 6 giây im lặng ⇒ <c>seed_answered = 2</c> ⇒
/// <c>74 × 2/3 = 49.33</c> thay vì <c>24.67</c>. Không biết thì bấm ghi im lặng có lợi +24,7 điểm.</para>
///
/// <para>Mỗi test dưới đây là MỘT nghĩa của <c>Skipped</c> — đó là điểm phân biệt, không phải trạng thái.
/// Đi trọn đường thật <see cref="SessionScoringNotifier"/> → outbox, không gọi thẳng
/// <c>ScoringContext</c>.</para>
/// </summary>
public class NoSpeechNotAnsweredCamp21Tests
{
    // Buổi B2B skip_penalty=true ⇒ SkipPenaltyRule TỰ nhân seed_completeness sau khi đánh giá biểu thức.
    // Biểu thức chỉ là `weighted_avg_pct` — KHÔNG được nhét thêm `* seed_completeness` vào đây, kẻo nhân
    // hai lần (80 × 1/3 × 1/3): lượt viết đầu của test này đã dính đúng thế.
    private const string Expr = "weighted_avg_pct";

    // 1 tiêu chí weight 1.0 maxScore 5 ⇒ weighted_avg_pct = 80.
    // `build` nhận (sessionId, questionId, i) và trả answer cho câu thứ i (null = không tạo hàng).
    private static (TestDb T, Guid SessionId) Seed(
        int seedTotal, Func<Guid, Guid, int, PracticeAnswer?> build)
    {
        var t = new TestDb();
        var campaignId = Guid.NewGuid();
        var crit = TestDb.Criterion(JobCategory.BE, version: 1, campaignId: campaignId, name: "Clarity");
        crit.MaxScore = 5;
        crit.Weight = 1.0m;
        t.Db.Add(crit);

        var session = TestDb.Session(Guid.NewGuid(), SessionStatus.Scored, campaignId: campaignId);
        session.CampaignRubricVersion = 1;
        session.SkipPenalty = true;
        session.CampaignPolicyExpression = Expr;
        session.CampaignPolicyVersion = 1;
        session.CampaignPolicyEngineVersion = ScoringEngine.Version;
        t.Db.Add(session);

        for (var i = 0; i < seedTotal; i++)
        {
            var q = TestDb.Question(session.Id, i + 1);
            q.Kind = QuestionKind.Seed;
            t.Db.Add(q);

            var a = build(session.Id, q.Id, i);
            if (a is null) continue;
            t.Db.Add(a);
            if (a.Status == AnswerStatus.Scored)
                t.Db.AnswerScores.Add(new AnswerScore
                {
                    Id = Guid.NewGuid(), AnswerId = a.Id, CriterionId = crit.Id,
                    AttemptNo = 1, Score = 4m, Reasoning = "ok", RubricVersion = 1, CreatedAt = DateTime.UtcNow
                });
        }

        t.Db.SaveChanges();
        return (t, session.Id);
    }

    private static PracticeAnswer Scored(Guid s, Guid q)
        => TestDb.Answer(s, q, AnswerStatus.Scored, DateTime.UtcNow, DateTime.UtcNow);

    private static PracticeAnswer SilentWithAudio(Guid s, Guid q)
        => TestDb.Answer(s, q, AnswerStatus.Skipped, DateTime.UtcNow, DateTime.UtcNow,
            rejectReason: AnswerService.NoSpeechReason);

    private static PracticeAnswer ClosedOutWithAudio(Guid s, Guid q)
        => TestDb.Answer(s, q, AnswerStatus.Skipped, DateTime.UtcNow, DateTime.UtcNow);   // reason null

    private static PracticeAnswer NeverRecorded(Guid s, Guid q)
        => TestDb.Answer(s, q, AnswerStatus.Skipped, DateTime.UtcNow, null, audioObjectKey: null);

    private static PracticeAnswer JunkTranscript(Guid s, Guid q)
        => TestDb.Answer(s, q, AnswerStatus.Failed, DateTime.UtcNow, DateTime.UtcNow);   // reason null

    private static async Task<Isas.InterviewService.DTOs.SessionScoredEvent> Score(TestDb t, Guid sessionId)
    {
        await TestDb.Notifier(t.Db).EnqueueSessionScoredAsync(sessionId);
        await t.Db.SaveChangesAsync();
        return TestDb.ScoredOutbox(t.NewContext(), sessionId)!;
    }

    // ── Ca đã tìm ra lỗi: 1 trả lời + 1 im lặng + 1 bỏ trống ⇒ seed_answered = 1, KHÔNG phải 2 ──────
    [Fact]
    public async Task ImLang_CoAudio_KhongTinhLaDaTraLoi()
    {
        var (t, s) = Seed(3, (sid, qid, i) => i switch
        {
            0 => Scored(sid, qid),
            1 => SilentWithAudio(sid, qid),
            _ => NeverRecorded(sid, qid),
        });
        using var _ = t;

        var evt = await Score(t, s);

        Assert.False(evt.ScoreFallback);
        Assert.Equal(1, evt.ScoringInputs!.SeedAnswered);   // trước bản vá: 2
        Assert.Equal(3, evt.ScoringInputs.SeedTotal);
        Assert.Equal(1, evt.ScoringInputs.Answered);        // CẢ HAI đại lượng cùng vị ngữ
        Assert.Equal(3, evt.ScoringInputs.TotalQuestions);
        // 80 × 1/3 = 26.666… (trước bản vá: 80 × 2/3 = 53.33). So sánh theo tỉ lệ, không ghim làm tròn.
        Assert.Equal(Math.Round(80m / 3m, 2), Math.Round(evt.TotalScore, 2));
    }

    // ── Nghĩa (b): buổi kẹt bị chốt sổ — lỗi của BỘ CHẤM, VẪN tính là đã trả lời ─────────────────
    [Fact]
    public async Task ChotSoBuoiKet_CoAudio_ReasonNull_VanTinhLaDaTraLoi()
    {
        var (t, s) = Seed(3, (sid, qid, i) => i switch
        {
            0 => Scored(sid, qid),
            1 => ClosedOutWithAudio(sid, qid),
            _ => NeverRecorded(sid, qid),
        });
        using var _ = t;

        var evt = await Score(t, s);

        Assert.Equal(2, evt.ScoringInputs!.SeedAnswered);
        Assert.Equal(2, evt.ScoringInputs.Answered);
        Assert.Equal(Math.Round(80m * 2m / 3m, 2), Math.Round(evt.TotalScore, 2));
    }

    // ── Nghĩa (c): chưa từng ghi âm — vốn đã không tính, không đổi ───────────────────────────────
    [Fact]
    public async Task ChuaGhiAm_KhongAudio_KhongTinh()
    {
        var (t, s) = Seed(2, (sid, qid, i) => i == 0 ? Scored(sid, qid) : NeverRecorded(sid, qid));
        using var _ = t;

        var evt = await Score(t, s);

        Assert.Equal(1, evt.ScoringInputs!.SeedAnswered);
        Assert.Equal(1, evt.ScoringInputs.Answered);
    }

    // ── Bản chép rác → Failed (lỗi bộ chép của TA), reason null ⇒ VẪN tính ─────────────────────
    [Fact]
    public async Task BanChepRac_Failed_ReasonNull_VanTinhLaDaTraLoi()
    {
        var (t, s) = Seed(2, (sid, qid, i) => i == 0 ? Scored(sid, qid) : JunkTranscript(sid, qid));
        using var _ = t;

        var evt = await Score(t, s);

        Assert.Equal(2, evt.ScoringInputs!.SeedAnswered);
        Assert.Equal(2, evt.ScoringInputs.Answered);
    }

    // ── KHÔNG HỒI TỐ: dòng CŨ (trước migration) = Skipped + có audio + reject_reason NULL ⇒ VẪN tính.
    //    Về dữ liệu, ca này TRÙNG với "chốt sổ" ở trên — cố ý giữ riêng vì đây là lời hứa KHÁC:
    //    "không dòng lịch sử nào bị đổi điểm". Nếu vị ngữ SQL mất vế IS NULL thì hai test này đỏ cùng lúc.
    [Fact]
    public async Task DongCu_ReasonNull_KhongDoiDiemHoiTo()
    {
        var (t, s) = Seed(2, (sid, qid, i) => i == 0 ? Scored(sid, qid) : ClosedOutWithAudio(sid, qid));
        using var _ = t;

        var evt = await Score(t, s);

        Assert.Equal(2, evt.ScoringInputs!.SeedAnswered);
        Assert.Equal(80m, evt.TotalScore);   // seed_completeness = 1 ⇒ không phạt, y hệt trước bản vá
    }

    // ── Vị ngữ SQL THẬT phải bù NULL: `reject_reason IS NULL OR reject_reason <> 'no_speech'` ─────
    //    EF Core mặc định (không UseRelationalNulls) tự bù null-semantics C#, nên ngay cả `!=` trần cũng
    //    dịch ra có IS NULL. Test này khoá HÀNH VI ở tầng SQL — không phụ thuộc vào việc bù đó đến từ
    //    code tường minh hay từ EF: bật UseRelationalNulls / viết raw SQL / đổi cách so mà làm mất vế
    //    IS NULL là mọi dòng cũ bị lọc (NULL <> 'x' = UNKNOWN) ⇒ đổi điểm hồi tố toàn bộ lịch sử.
    [Fact]
    public void ViNguSql_PhaiCoVeIsNull_DeKhongLocMatDongCu()
    {
        using var t = new TestDb();
        var sql = t.Db.PracticeAnswers.Where(SessionScoringNotifier.AnsweredPredicate).ToQueryString();

        Assert.Contains("reject_reason", sql);
        Assert.Contains("IS NULL", sql, StringComparison.OrdinalIgnoreCase);
        Assert.Contains("audio_object_key", sql);
    }

    // ── answered và seedAnswered dùng CÙNG MỘT vị ngữ ─────────────────────────────────────────────
    //    Chống việc sau này ai đó "sửa" một vế mà quên vế kia: hai đại lượng cùng giảm đúng 1 khi thêm
    //    đúng 1 bài im lặng, trên cùng một buổi. (Test đầu đã assert cả hai; đây là phép đối chứng vi sai.)
    [Fact]
    public async Task Answered_VaSeedAnswered_CungGiamKhiThemBaiImLang()
    {
        var (t0, s0) = Seed(2, (sid, qid, _) => Scored(sid, qid));
        using var _0 = t0;
        var (t1, s1) = Seed(2, (sid, qid, i) => i == 0 ? Scored(sid, qid) : SilentWithAudio(sid, qid));
        using var _1 = t1;

        var e0 = await Score(t0, s0);
        var e1 = await Score(t1, s1);

        Assert.Equal(e0.ScoringInputs!.Answered - 1, e1.ScoringInputs!.Answered);
        Assert.Equal(e0.ScoringInputs.SeedAnswered - 1, e1.ScoringInputs.SeedAnswered);
        Assert.Equal(e0.ScoringInputs.Answered, e0.ScoringInputs.SeedAnswered);
        Assert.Equal(e1.ScoringInputs.Answered, e1.ScoringInputs.SeedAnswered);
    }
}
