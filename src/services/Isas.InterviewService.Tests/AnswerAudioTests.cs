using Isas.InterviewService.Entities;
using Isas.InterviewService.Enums;
using Isas.InterviewService.Services;
using Isas.InterviewService.Services.Interfaces;
using Microsoft.Extensions.Logging.Abstractions;
using Moq;

namespace Isas.InterviewService.Tests;

public class AnswerAudioTests
{
    private static PracticeService Build(TestDb t, Mock<IStorageService> storage) =>
        new(t.Db, storage.Object, new Mock<IAiServiceQuestionGenerator>().Object,
            new Mock<ISessionScoringNotifier>().Object, new Mock<ICreditReservationClient>().Object,
            NullLogger<PracticeService>.Instance);

    [Fact]
    public async Task ChuBuoi_LayDuocAudioCauTraLoi()
    {
        using var t = new TestDb();
        var candidate = Guid.NewGuid();
        var session = TestDb.Session(candidate, SessionStatus.InProgress);
        var question = TestDb.Question(session.Id);
        var answer = TestDb.Answer(session.Id, question.Id, AnswerStatus.Uploaded, DateTime.UtcNow, null);
        answer.AudioObjectKey = "answer-audio/mine.webm";
        t.Db.AddRange(session, question, answer);
        await t.Db.SaveChangesAsync();

        var storage = new Mock<IStorageService>();
        storage.Setup(s => s.DownloadAsync(answer.AudioObjectKey!, It.IsAny<CancellationToken>()))
            .ReturnsAsync(new MemoryStream([1, 2, 3]));

        var result = await Build(t, storage).GetAnswerAudioAsync(candidate, session.Id, answer.Id);

        Assert.NotNull(result);
        Assert.Equal("audio/webm", result!.ContentType);
        storage.Verify(s => s.DownloadAsync(answer.AudioObjectKey!, It.IsAny<CancellationToken>()), Times.Once);
    }

    [Fact]
    public async Task NguoiKhac_KhongLayDuocAudioVaKhongDocStorage()
    {
        using var t = new TestDb();
        var owner = Guid.NewGuid();
        var session = TestDb.Session(owner, SessionStatus.InProgress);
        var question = TestDb.Question(session.Id);
        var answer = TestDb.Answer(session.Id, question.Id, AnswerStatus.Uploaded, DateTime.UtcNow, null);
        answer.AudioObjectKey = "answer-audio/private.webm";
        t.Db.AddRange(session, question, answer);
        await t.Db.SaveChangesAsync();

        var storage = new Mock<IStorageService>();

        await Assert.ThrowsAsync<UnauthorizedAccessException>(() =>
            Build(t, storage).GetAnswerAudioAsync(Guid.NewGuid(), session.Id, answer.Id));

        storage.Verify(s => s.DownloadAsync(It.IsAny<string>(), It.IsAny<CancellationToken>()), Times.Never);
    }

    [Fact]
    public async Task AnswerChuaCoAudio_TraNull()
    {
        using var t = new TestDb();
        var candidate = Guid.NewGuid();
        var session = TestDb.Session(candidate, SessionStatus.InProgress);
        var question = TestDb.Question(session.Id);
        var answer = TestDb.Answer(session.Id, question.Id, AnswerStatus.Uploaded, DateTime.UtcNow, null);
        answer.AudioObjectKey = null;
        t.Db.AddRange(session, question, answer);
        await t.Db.SaveChangesAsync();

        var storage = new Mock<IStorageService>();
        var result = await Build(t, storage).GetAnswerAudioAsync(candidate, session.Id, answer.Id);

        Assert.Null(result);
        storage.Verify(s => s.DownloadAsync(It.IsAny<string>(), It.IsAny<CancellationToken>()), Times.Never);
    }
}
