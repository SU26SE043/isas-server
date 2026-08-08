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
/// F2 — ứng viên chọn thời lượng mỗi câu (60/120/240s) lúc tạo buổi luyện.
///
/// Hai điểm đáng test nhất, vì cả hai đều hỏng ÂM THẦM nếu sai:
///  (1) guard phải chạy TRƯỚC ReserveAsync — giá trị sai mà đã reserve thì ứng viên mất credit cho
///      một buổi luyện không bao giờ tồn tại (PAY-5);
///  (2) câu THÍCH ỨNG (sinh sau, ở AnswerService) phải kế thừa đúng lựa chọn — trước F2 nó dùng hằng
///      số 120 riêng, nên chọn 4 phút thì câu AI hỏi thêm vẫn chỉ cho 2 phút.
/// </summary>
public class SessionTimeLimitF2Tests
{
    private static Mock<ICreditReservationClient> CreditsMock()
    {
        var m = new Mock<ICreditReservationClient>();
        m.Setup(x => x.ReserveAsync(
                It.IsAny<string>(), It.IsAny<Guid>(), It.IsAny<Guid>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(new CreditReservationResult(Guid.NewGuid(), 1));
        return m;
    }

    private static Mock<IAiServiceQuestionGenerator> GeneratorMock()
    {
        var gen = new Mock<IAiServiceQuestionGenerator>();
        gen.Setup(g => g.GenerateQuestionsAsync(
                It.IsAny<string>(), It.IsAny<string?>(), It.IsAny<string?>(), It.IsAny<string>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(new List<GeneratedQuestion> { new() { Content = "Q1" }, new() { Content = "Q2" } });
        return gen;
    }

    private static PracticeService Build(TestDb t, Mock<ICreditReservationClient> credits)
        => new(
            t.Db, new Mock<IStorageService>().Object, GeneratorMock().Object,
            new Mock<ISessionScoringNotifier>().Object, credits.Object,
            NullLogger<PracticeService>.Instance);

    private static CreatePracticeSessionRequest Request(int? timeLimitSec)
        => new(null, null, JobCategory.BE, null, timeLimitSec);

    // ── Giá trị hợp lệ đóng dấu lên CẢ session lẫn từng câu seed ─────────────
    [Theory]
    [InlineData(60)]
    [InlineData(120)]
    [InlineData(240)]
    public async Task ValidChoice_StampsSessionAndSeedQuestions(int chosen)
    {
        using var t = new TestDb();
        var candidate = Guid.NewGuid();

        var res = await Build(t, CreditsMock()).CreateSessionAsync(candidate, Request(chosen));

        var session = await t.Db.PracticeSessions.AsNoTracking().FirstAsync(s => s.Id == res.Id);
        Assert.Equal(chosen, session.TimeLimitSec);

        var questions = await t.Db.PracticeQuestions.AsNoTracking()
            .Where(q => q.SessionId == res.Id).ToListAsync();
        Assert.NotEmpty(questions);
        Assert.All(questions, q => Assert.Equal(chosen, q.TimeLimitSec));
    }

    // ── Client cũ không gửi field → giữ NGUYÊN hành vi trước F2 (chống hồi quy) ──
    [Fact]
    public async Task Null_DefaultsTo120_NoBehaviourChange()
    {
        using var t = new TestDb();
        var candidate = Guid.NewGuid();

        var res = await Build(t, CreditsMock()).CreateSessionAsync(candidate, Request(null));

        var session = await t.Db.PracticeSessions.AsNoTracking().FirstAsync(s => s.Id == res.Id);
        Assert.Equal(120, session.TimeLimitSec);
        Assert.All(
            await t.Db.PracticeQuestions.AsNoTracking().Where(q => q.SessionId == res.Id).ToListAsync(),
            q => Assert.Equal(120, q.TimeLimitSec));
    }

    // ── Giá trị ngoài tập → 400 và TUYỆT ĐỐI không giữ credit (PAY-5) ────────
    [Theory]
    [InlineData(90)]
    [InlineData(0)]
    [InlineData(-1)]
    [InlineData(3600)]
    public async Task InvalidChoice_Throws_AndNeverReserves(int bad)
    {
        using var t = new TestDb();
        var credits = CreditsMock();

        await Assert.ThrowsAsync<InvalidOperationException>(
            () => Build(t, credits).CreateSessionAsync(Guid.NewGuid(), Request(bad)));

        // Guard phải nằm TRƯỚC reserve — nếu không, request sai vẫn trừ credit ví ứng viên.
        credits.Verify(
            x => x.ReserveAsync(
                It.IsAny<string>(), It.IsAny<Guid>(), It.IsAny<Guid>(), It.IsAny<CancellationToken>()),
            Times.Never);

        // Và không để lại session mồ côi.
        Assert.Empty(await t.Db.PracticeSessions.AsNoTracking().ToListAsync());
    }

    // ── Câu THÍCH ỨNG kế thừa lựa chọn của buổi, không phải hằng số 120 cũ ───
    [Fact]
    public async Task AdaptiveQuestion_InheritsSessionTimeLimit()
    {
        using var t = new TestDb();
        var candidate = Guid.NewGuid();

        var session = TestDb.Session(candidate, SessionStatus.Ready);
        session.AdaptiveEnabled = true;
        session.MaxQuestions = 10;
        session.MaxFollowUps = 3;
        session.TimeLimitSec = 240;   // ứng viên chọn 4 phút
        var q = TestDb.Question(session.Id);
        q.TimeLimitSec = 240;
        t.Db.AddRange(session, q, TestDb.Criterion(session.JobCategory));
        await t.Db.SaveChangesAsync();

        var decider = new Mock<IAiServiceInterviewDecider>();
        decider
            .Setup(x => x.DecideNextAsync(
                It.IsAny<AdaptiveDecisionRequest>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(new DecideNextResult("follow_up", "Cho ví dụ cụ thể?", "transcript", "đào sâu"));

        var storage = new Mock<IStorageService>();
        storage
            .Setup(s => s.UploadAsync(
                It.IsAny<Stream>(), It.IsAny<string>(), It.IsAny<Guid>(),
                It.IsAny<Guid>(), It.IsAny<string>(), It.IsAny<string>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync("answer-audio/seed.webm");

        var svc = new AnswerService(
            t.Db, storage.Object, new Mock<IScoringJobPublisher>().Object,
            new Mock<ISessionScoringNotifier>().Object, TestDb.ScoringOpts(),
            NullLogger<AnswerService>.Instance, decider.Object);

        using var audio = new MemoryStream(new byte[] { 1 });
        await svc.UploadAnswerAsync(session.Id, q.Id, candidate, audio, "audio/webm", 30);

        var appended = await t.Db.PracticeQuestions.AsNoTracking()
            .Where(x => x.SessionId == session.Id)
            .OrderBy(x => x.OrderNo)
            .LastAsync();
        Assert.Equal(240, appended.TimeLimitSec);
    }
}
