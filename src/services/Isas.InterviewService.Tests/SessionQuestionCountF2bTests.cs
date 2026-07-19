using Isas.InterviewService.DTOs;
using Isas.InterviewService.Enums;
using Isas.InterviewService.Models;
using Isas.InterviewService.Services;
using Isas.InterviewService.Services.Interfaces;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using Moq;

namespace Isas.InterviewService.Tests;

/// <summary>
/// F2b — ứng viên chọn số câu hỏi cho buổi luyện, trần 1..20.
///
/// Test then chốt phần .NET; phần "AIService có thật sự sinh đúng số câu không" nằm ở pytest
/// (tests/test_generate_questions.py) vì đó là nơi `count` được đưa vào prompt và cắt danh sách.
/// Chia vậy có lý do: nếu chỉ test .NET thì một thay đổi làm AIService bỏ qua `count` vẫn XANH —
/// đúng kiểu hỏng âm thầm mà tính năng này dễ mắc nhất (adaptive đang tắt mặc định).
/// </summary>
public class SessionQuestionCountF2bTests
{
    private static Mock<ICreditReservationClient> CreditsMock()
    {
        var m = new Mock<ICreditReservationClient>();
        m.Setup(x => x.ReserveAsync(
                It.IsAny<string>(), It.IsAny<Guid>(), It.IsAny<Guid>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(new CreditReservationResult(Guid.NewGuid(), 1));
        return m;
    }

    /// <summary>Mock cả 2 overload; ghi lại `count` mà production thực sự gửi xuống AIService.</summary>
    private static Mock<IAiServiceQuestionGenerator> GeneratorMock(Action<int?>? captureCount = null)
    {
        var gen = new Mock<IAiServiceQuestionGenerator>();
        var questions = new List<GeneratedQuestion> { new() { Content = "Q1" }, new() { Content = "Q2" } };

        gen.Setup(g => g.GenerateQuestionsAsync(
                It.IsAny<string>(), It.IsAny<string?>(), It.IsAny<string?>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(questions);

        gen.Setup(g => g.GenerateQuestionsAsync(
                It.IsAny<string>(), It.IsAny<string?>(), It.IsAny<string?>(),
                It.IsAny<IReadOnlyList<string>?>(), It.IsAny<int?>(), It.IsAny<CancellationToken>()))
            .Callback<string, string?, string?, IReadOnlyList<string>?, int?, CancellationToken>(
                (_, _, _, _, count, _) => captureCount?.Invoke(count))
            .ReturnsAsync(questions);

        return gen;
    }

    private static PracticeService Build(
        TestDb t, Mock<ICreditReservationClient> credits, Mock<IAiServiceQuestionGenerator> gen,
        AdaptiveOptions? adaptive = null)
        => new(
            t.Db, new Mock<IStorageService>().Object, gen.Object,
            new Mock<ISessionScoringNotifier>().Object, credits.Object,
            NullLogger<PracticeService>.Instance,
            adaptive is null ? null : Options.Create(adaptive));

    private static CreatePracticeSessionRequest Request(int? questionCount)
        => new(null, null, JobCategory.BE, null, null, questionCount);

    // ── Số câu ứng viên chọn phải THỰC SỰ đi xuống AIService ────────────────
    [Theory]
    [InlineData(1)]
    [InlineData(8)]
    [InlineData(20)]
    public async Task ChosenCount_IsForwardedToAiService(int chosen)
    {
        using var t = new TestDb();
        int? sent = null;
        var gen = GeneratorMock(c => sent = c);

        await Build(t, CreditsMock(), gen).CreateSessionAsync(Guid.NewGuid(), Request(chosen));

        Assert.Equal(chosen, sent);
    }

    // ── Không chọn → KHÔNG ghi đè mặc định AIService (giữ nguyên hành vi trước F2b) ──
    [Fact]
    public async Task Null_KeepsLegacyPath_AndDoesNotOverrideAiDefault()
    {
        using var t = new TestDb();
        var gen = GeneratorMock();

        await Build(t, CreditsMock(), gen).CreateSessionAsync(Guid.NewGuid(), Request(null));

        // Đi đúng overload cũ (4 tham số) — không gửi count nào cả.
        gen.Verify(g => g.GenerateQuestionsAsync(
            It.IsAny<string>(), It.IsAny<string?>(), It.IsAny<string?>(), It.IsAny<CancellationToken>()),
            Times.Once);
        gen.Verify(g => g.GenerateQuestionsAsync(
            It.IsAny<string>(), It.IsAny<string?>(), It.IsAny<string?>(),
            It.IsAny<IReadOnlyList<string>?>(), It.IsAny<int?>(), It.IsAny<CancellationToken>()),
            Times.Never);
    }

    // ── Ngoài 1..20 → 400 và KHÔNG giữ credit (PAY-5) ───────────────────────
    [Theory]
    [InlineData(0)]
    [InlineData(21)]
    [InlineData(-3)]
    [InlineData(100_000)]
    public async Task OutOfRange_Throws_AndNeverReserves(int bad)
    {
        using var t = new TestDb();
        var credits = CreditsMock();
        var gen = GeneratorMock();

        await Assert.ThrowsAsync<InvalidOperationException>(
            () => Build(t, credits, gen).CreateSessionAsync(Guid.NewGuid(), Request(bad)));

        credits.Verify(
            x => x.ReserveAsync(
                It.IsAny<string>(), It.IsAny<Guid>(), It.IsAny<Guid>(), It.IsAny<CancellationToken>()),
            Times.Never);
        Assert.Empty(await t.Db.PracticeSessions.AsNoTracking().ToListAsync());
    }

    // ── Adaptive BẬT: trần tổng số câu của buổi bám lựa chọn, không phải cấu hình ──
    [Fact]
    public async Task AdaptiveEnabled_MaxQuestionsFollowsChoice()
    {
        using var t = new TestDb();
        var adaptive = new AdaptiveOptions { Enabled = true, SeedCount = 1, MaxQuestions = 10, MaxFollowUps = 3 };

        var res = await Build(t, CreditsMock(), GeneratorMock(), adaptive)
            .CreateSessionAsync(Guid.NewGuid(), Request(7));

        var session = await t.Db.PracticeSessions.AsNoTracking().FirstAsync(s => s.Id == res.Id);
        Assert.True(session.AdaptiveEnabled);
        Assert.Equal(7, session.MaxQuestions);
    }

    // ── Adaptive BẬT nhưng không chọn → rơi về cấu hình (không đổi hành vi cũ) ──
    [Fact]
    public async Task AdaptiveEnabled_NoChoice_FallsBackToConfig()
    {
        using var t = new TestDb();
        var adaptive = new AdaptiveOptions { Enabled = true, SeedCount = 1, MaxQuestions = 10, MaxFollowUps = 3 };

        var res = await Build(t, CreditsMock(), GeneratorMock(), adaptive)
            .CreateSessionAsync(Guid.NewGuid(), Request(null));

        var session = await t.Db.PracticeSessions.AsNoTracking().FirstAsync(s => s.Id == res.Id);
        Assert.Equal(10, session.MaxQuestions);
    }
}
