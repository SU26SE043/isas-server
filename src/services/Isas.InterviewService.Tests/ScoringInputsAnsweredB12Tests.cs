using Isas.InterviewService.Entities;
using Isas.InterviewService.Enums;
using Isas.InterviewService.Services;
using Isas.InterviewService.Services.Interfaces;
using Isas.Shared.Scoring;
using Microsoft.Extensions.Logging.Abstractions;
using Moq;
using Xunit;

namespace Isas.InterviewService.Tests;

/// <summary>
/// SCP1-B12 — <c>scoring_inputs.Answered</c> = số câu ứng viên THỰC SỰ trả lời (có ghi âm), KHÔNG
/// phải mọi hàng <c>practice_answers</c>. Trước B12, <c>MarkUnansweredAsSkippedAsync</c> tạo hàng
/// thật (AudioObjectKey NULL) cho câu chưa trả lời ⇒ <c>answered == totalQuestions</c> ở 100% buổi
/// được chấm ⇒ biến <c>completeness</c> luôn = 1 ⇒ mẫu chính sách "phạt bỏ câu" vô hiệu.
///
/// Test đi TRỌN đường thật: <see cref="PracticeService.SubmitSessionAsync"/> → outbox SessionScored →
/// đọc <c>ScoringInputs</c> ra khỏi payload. KHÔNG gọi thẳng <c>ScoringContext.ForInterview</c>.
/// </summary>
public class ScoringInputsAnsweredB12Tests
{
    // Buổi B2B (campaignId) để ghim chính sách đồng nhất. `questions` câu; `scoredWithAudio` câu đầu
    // trả lời + chấm (Scored, có audio); `skippedWithAudio` câu kế = Skipped NHƯNG có audio (mô phỏng
    // đường VAD im lặng / chốt sổ buổi kẹt AnswerService.cs:392/:1627/:1682); phần còn lại bỏ trống
    // ⇒ SubmitSession tự tạo hàng Skipped KHÔNG audio (MarkUnansweredAsSkippedAsync).
    private static (TestDb T, Guid SessionId, Guid Candidate) Seed(
        int questions, int scoredWithAudio, int skippedWithAudio = 0,
        string? policyExpr = null, decimal score = 4m)
    {
        var t = new TestDb();
        var candidate = Guid.NewGuid();
        var campaignId = Guid.NewGuid();

        var session = TestDb.Session(candidate, SessionStatus.InProgress, JobCategory.BE, campaignId: campaignId);
        session.CampaignRubricVersion = 1;
        if (policyExpr is not null)
        {
            session.CampaignPolicyExpression = policyExpr;
            session.CampaignPolicyVersion = 1;
            session.CampaignPolicyEngineVersion = ScoringEngine.Version;
        }
        t.Db.Add(session);

        var crit = TestDb.Criterion(JobCategory.BE, version: 1, campaignId: campaignId, name: "Clarity");
        crit.MaxScore = 5;
        crit.Weight = 1.0m;
        t.Db.Add(crit);

        var now = DateTime.UtcNow;
        for (var i = 0; i < questions; i++)
        {
            var q = TestDb.Question(session.Id, i + 1);
            t.Db.Add(q);

            if (i < scoredWithAudio)
            {
                var a = TestDb.Answer(session.Id, q.Id, AnswerStatus.Scored, now, now);   // TestDb.Answer luôn gán AudioObjectKey
                t.Db.Add(a);
                t.Db.AnswerScores.Add(new AnswerScore
                {
                    Id = Guid.NewGuid(), AnswerId = a.Id, CriterionId = crit.Id,
                    AttemptNo = 1, Score = score, Reasoning = "ok", RubricVersion = 1, CreatedAt = now
                });
            }
            else if (i < scoredWithAudio + skippedWithAudio)
            {
                // Skipped NHƯNG có audio — ứng viên ĐÃ ghi âm, chỉ là bộ chấm/VAD của ta không dùng được.
                t.Db.Add(TestDb.Answer(session.Id, q.Id, AnswerStatus.Skipped, now, now));
            }
            // else: bỏ trống → SubmitSession tạo Skipped KHÔNG audio.
        }

        t.Db.SaveChanges();
        return (t, session.Id, candidate);
    }

    private static async Task<Isas.InterviewService.DTOs.SessionScoredEvent> Submit(TestDb t, Guid sessionId, Guid candidate)
    {
        var svc = new PracticeService(
            t.Db, new Mock<IStorageService>().Object, new Mock<IAiServiceQuestionGenerator>().Object,
            TestDb.Notifier(t.Db), new Mock<ICreditReservationClient>().Object,
            NullLogger<PracticeService>.Instance);

        await svc.SubmitSessionAsync(candidate, sessionId);
        return TestDb.ScoredOutbox(t.NewContext(), sessionId)!;
    }

    // (a) buổi 4 câu, trả lời 2, nộp → scoring_inputs.Answered = 2, TotalQuestions = 4.
    [Fact]
    public async Task Buoi_4_cau_tra_loi_2_thi_Answered_la_2()
    {
        var (t, s, cand) = Seed(questions: 4, scoredWithAudio: 2);
        using var _ = t;

        var evt = await Submit(t, s, cand);

        Assert.NotNull(evt.ScoringInputs);
        Assert.Equal(2, evt.ScoringInputs!.Answered);
        Assert.Equal(4, evt.ScoringInputs.TotalQuestions);
    }

    // (b) cùng buổi đó với "weighted_avg_pct * completeness" → điểm = weighted * 0.5, KHÁC
    //     "weighted_avg_pct" (chứng minh completeness THỰC SỰ = 0.5, không phải 1).
    [Fact]
    public async Task Completeness_ha_diem_theo_ty_le_cau_tra_loi()
    {
        var (tHalf, sHalf, cHalf) = Seed(questions: 4, scoredWithAudio: 2, policyExpr: "weighted_avg_pct * completeness");
        using var _h = tHalf;
        var (tFull, sFull, cFull) = Seed(questions: 4, scoredWithAudio: 2, policyExpr: "weighted_avg_pct");
        using var _f = tFull;

        var half = await Submit(tHalf, sHalf, cHalf);
        var full = await Submit(tFull, sFull, cFull);

        Assert.Equal(80m, full.TotalScore);              // weighted_avg_pct = 4/5 = 80%
        Assert.Equal(40m, half.TotalScore);              // 80 * (2/4)
        Assert.Equal(full.TotalScore / 2m, half.TotalScore);
        Assert.False(half.ScoreFallback);
    }

    // (c) buổi trả lời đủ 4/4 → completeness = 1 (không hồi quy — biểu thức có completeness = biểu thức không).
    [Fact]
    public async Task Buoi_tra_loi_du_thi_completeness_bang_1()
    {
        var (t, s, cand) = Seed(questions: 4, scoredWithAudio: 4, policyExpr: "weighted_avg_pct * completeness");
        using var _ = t;

        var evt = await Submit(t, s, cand);

        Assert.Equal(4, evt.ScoringInputs!.Answered);
        Assert.Equal(4, evt.ScoringInputs.TotalQuestions);
        Assert.Equal(80m, evt.TotalScore);              // 80 * 1
    }

    // (d) buổi có 2 câu chấm được + 2 câu Skipped NHƯNG CÓ AUDIO (đường chốt sổ buổi kẹt
    //     AnswerService.cs:1682) → 2 câu đó VẪN tính là "đã trả lời" ⇒ completeness KHÔNG bị hạ.
    [Fact]
    public async Task Skipped_co_audio_khong_ha_completeness()
    {
        var (t, s, cand) = Seed(questions: 4, scoredWithAudio: 2, skippedWithAudio: 2,
            policyExpr: "weighted_avg_pct * completeness");
        using var _ = t;

        var evt = await Submit(t, s, cand);

        Assert.Equal(4, evt.ScoringInputs!.Answered);   // cả 4 câu đều có ghi âm
        Assert.Equal(4, evt.ScoringInputs.TotalQuestions);
        Assert.Equal(80m, evt.TotalScore);              // completeness = 1 ⇒ không phạt
    }
}
