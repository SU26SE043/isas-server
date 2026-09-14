using System.Text.Json;
using Isas.InterviewService.ApplicationDbContext;
using Isas.InterviewService.DTOs;
using Isas.InterviewService.Entities;
using Isas.InterviewService.Enums;
using Isas.InterviewService.Services;
using Isas.InterviewService.Services.Interfaces;
using Microsoft.Extensions.Logging.Abstractions;
using Moq;

namespace Isas.InterviewService.Tests;

/// <summary>
/// EVA1-B4 / CAMP-15 — ứng viên B2B là CHỦ session (Start trả về sessionId) nên đọc được qua
/// <c>GetSessionAsync</c>, KHÔNG bị chặn truy cập (họ cần endpoint này để lấy câu hỏi mà làm bài).
/// Nhưng nội bộ chấm điểm phải che: điểm/reasoning/levelMatched per-criterion, cờ needs_review,
/// và đáp án mẫu (bộ câu campaign là bộ CHUNG ⇒ người thi TRƯỚC đọc được đáp án mẫu của đúng bộ
/// người thi SAU). Khối tổng kết (<c>MapResult</c>) đã chặn B2B từ trước — hở ở mức chi tiết là
/// ngoài ý muốn.
///
/// <para>⚠ Sentinel phải là ASCII: <c>System.Text.Json</c> escape ký tự non-ASCII (\u...), nên
/// assert một chuỗi tiếng Việt vào JSON đã serialize sẽ XANH một cách tầm thường KỂ CẢ KHI dữ
/// liệu đã rò. Mẫu: <c>Isas.CampaignService.Tests/CandidateCriterionLeakTests</c>.</para>
/// </summary>
public class SessionScoringLeakEva1B4Tests
{
    private const string ReasoningSentinel = "REASONING-SENTINEL-0001";
    private const string SampleSentinel = "SAMPLE-SENTINEL-0001";

    private static PracticeService BuildPractice(InterviewDbContext db)
    {
        var notifier = new Mock<ISessionScoringNotifier>();
        notifier
            .Setup(n => n.NotifySessionScoredAsync(It.IsAny<Guid>(), It.IsAny<CancellationToken>()))
            .Returns(Task.CompletedTask);

        var reservation = new Mock<ICreditReservationClient>();
        reservation
            .Setup(r => r.ReserveAsync(It.IsAny<string>(), It.IsAny<Guid>(), It.IsAny<Guid>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(new CreditReservationResult(Guid.NewGuid(), 1));

        return new PracticeService(
            db, new Mock<IStorageService>().Object,
            new Mock<IAiServiceQuestionGenerator>().Object,
            notifier.Object, reservation.Object,
            NullLogger<PracticeService>.Instance);
    }

    /// <summary>1 buổi đã Scored, 1 câu có answer mang transcript + điểm + reasoning + sample + needs_review.</summary>
    private static (Guid sessionId, Guid candidateId, Guid questionId) SeedScoredSession(TestDb t, Guid? campaignId)
    {
        var candidate = Guid.NewGuid();
        var session = TestDb.Session(candidate, SessionStatus.Scored, campaignId: campaignId);
        var q1 = TestDb.Question(session.Id, 1);
        var crit = TestDb.Criterion(session.JobCategory, campaignId: campaignId, name: "Technical depth");

        var answer = TestDb.Answer(session.Id, q1.Id, AnswerStatus.Scored, DateTime.UtcNow, DateTime.UtcNow);
        answer.Transcript = "Ứng viên giải thích dependency injection qua constructor.";
        answer.NeedsReview = true;                 // E10 — phải bị che ở đường ứng viên B2B
        answer.SampleAnswer = SampleSentinel;      // F13 — phải bị che ở đường ứng viên B2B

        var score = new AnswerScore
        {
            Id = Guid.NewGuid(),
            AnswerId = answer.Id,
            CriterionId = crit.Id,
            Score = 4m,
            Reasoning = ReasoningSentinel,          // E11 — phải bị che ở đường ứng viên B2B
            LevelMatched = 3,                       // E9 — nằm trong Scores → biến mất khi Scores rỗng
            AttemptNo = 1,
            RubricVersion = 1,
            CreatedAt = DateTime.UtcNow
        };

        using var seed = t.NewContext();
        seed.AddRange(session, q1, crit, answer, score);
        seed.SaveChanges();
        return (session.Id, candidate, q1.Id);
    }

    // Buổi B2B → GetSessionAsync (đường ứng viên) che ĐÚNG 4 thứ: perCriterion rỗng, needsReview
    // false, sampleAnswer null, levelMatched biến mất cùng Scores. Transcript/câu hỏi GIỮ (bài làm
    // của chính họ).
    [Fact]
    public async Task GetSessionAsync_BuoiB2B_Che_diem_reasoning_sample_needsReview()
    {
        using var t = new TestDb();
        var (sessionId, candidateId, questionId) = SeedScoredSession(t, campaignId: Guid.NewGuid());

        var res = await BuildPractice(t.NewContext()).GetSessionAsync(candidateId, sessionId);
        Assert.NotNull(res);

        var json = JsonSerializer.Serialize(res);
        Assert.DoesNotContain(ReasoningSentinel, json);
        Assert.DoesNotContain(SampleSentinel, json);

        var answer = res!.Questions.Single(q => q.Id == questionId).Answer;
        Assert.NotNull(answer);
        Assert.Empty(answer!.Scores);              // perCriterion + levelMatched biến mất
        Assert.False(answer.NeedsReview);
        Assert.Null(answer.SampleAnswer);

        // Đối chứng NỘI: KHÔNG che bài làm của chính ứng viên (không phải rỗng cho mọi thứ).
        Assert.Contains("constructor", answer.Transcript);
        Assert.Equal("Giải thích dependency injection?", res.Questions.Single(q => q.Id == questionId).Content);
    }

    // ĐỐI CHỨNG BẮT BUỘC: cùng dữ liệu, buổi B2C → CÓ cả hai sentinel + Scores không rỗng. Thiếu
    // test này thì test trên xanh cả khi endpoint trả rỗng cho MỌI người.
    [Fact]
    public async Task GetSessionAsync_BuoiB2C_GIU_nguyen_diem_reasoning_sample()
    {
        using var t = new TestDb();
        var (sessionId, candidateId, questionId) = SeedScoredSession(t, campaignId: null);

        var res = await BuildPractice(t.NewContext()).GetSessionAsync(candidateId, sessionId);
        Assert.NotNull(res);

        var json = JsonSerializer.Serialize(res);
        Assert.Contains(ReasoningSentinel, json);
        Assert.Contains(SampleSentinel, json);

        var answer = res!.Questions.Single(q => q.Id == questionId).Answer;
        Assert.NotNull(answer);
        var sc = Assert.Single(answer!.Scores);
        Assert.Equal(4m, sc.Score);
        Assert.Equal(ReasoningSentinel, sc.Reasoning);
        Assert.Equal(3, sc.LevelMatched);
        Assert.True(answer.NeedsReview);
        Assert.Equal(SampleSentinel, answer.SampleAnswer);
    }
}
