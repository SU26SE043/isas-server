using Isas.InterviewService.DTOs;
using Isas.InterviewService.Entities;
using Isas.InterviewService.Enums;
using Isas.InterviewService.Services;
using Isas.InterviewService.Services.Interfaces;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging.Abstractions;
using Moq;

namespace Isas.InterviewService.Tests;

public class AnswerServiceTests
{
    private static AnswerService Build(
        TestDb t, Mock<IScoringJobPublisher> publisher, out Mock<IStorageService> storage)
    {
        storage = new Mock<IStorageService>();
        storage
            .Setup(s => s.UploadAsync(
                It.IsAny<Stream>(), It.IsAny<string>(), It.IsAny<Guid>(),
                It.IsAny<Guid>(), It.IsAny<string>(), It.IsAny<string>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync("answer-audio/seed.webm");

        return new AnswerService(
            t.Db, storage.Object, publisher.Object, NullLogger<AnswerService>.Instance);
    }

    [Fact]
    public async Task Upload_PublishSucceeds_AnswerBecomesScoring_AndMarkerSet()
    {
        using var t = new TestDb();
        var candidate = Guid.NewGuid();
        var session = TestDb.Session(candidate, SessionStatus.Ready);
        var q = TestDb.Question(session.Id);
        var crit = TestDb.Criterion(session.JobCategory);
        t.Db.AddRange(session, q, crit);
        await t.Db.SaveChangesAsync();

        var publisher = new Mock<IScoringJobPublisher>();
        var svc = Build(t, publisher, out _);

        using var audio = new MemoryStream(new byte[] { 1, 2, 3 });
        var result = await svc.UploadAnswerAsync(
            session.Id, q.Id, candidate, audio, "audio/webm", 30);

        var saved = await t.Db.PracticeAnswers.AsNoTracking().FirstAsync(a => a.Id == result.AnswerId);
        Assert.Equal(AnswerStatus.Scoring, saved.Status);
        Assert.NotNull(saved.LastScoringPublishedAt);

        // Session Ready -> InProgress
        var s = await t.Db.PracticeSessions.AsNoTracking().FirstAsync(x => x.Id == session.Id);
        Assert.Equal(SessionStatus.InProgress, s.Status);

        publisher.Verify(p => p.PublishAsync(It.IsAny<ScoringJob>(), It.IsAny<CancellationToken>()), Times.Once);
    }

    [Fact]
    public async Task Upload_PublishThrows_AnswerStaysUploaded_NoMarker_NoException()
    {
        using var t = new TestDb();
        var candidate = Guid.NewGuid();
        var session = TestDb.Session(candidate, SessionStatus.Ready);
        var q = TestDb.Question(session.Id);
        var crit = TestDb.Criterion(session.JobCategory);
        t.Db.AddRange(session, q, crit);
        await t.Db.SaveChangesAsync();

        var publisher = new Mock<IScoringJobPublisher>();
        publisher
            .Setup(p => p.PublishAsync(It.IsAny<ScoringJob>(), It.IsAny<CancellationToken>()))
            .ThrowsAsync(new Exception("RabbitMQ down"));
        var svc = Build(t, publisher, out _);

        using var audio = new MemoryStream(new byte[] { 1 });
        // Publish hụt KHÔNG được làm hỏng upload.
        var result = await svc.UploadAnswerAsync(
            session.Id, q.Id, candidate, audio, "audio/webm", 30);

        var saved = await t.Db.PracticeAnswers.AsNoTracking().FirstAsync(a => a.Id == result.AnswerId);
        Assert.Equal(AnswerStatus.Uploaded, saved.Status);
        Assert.Null(saved.LastScoringPublishedAt);  // tín hiệu publish-hụt cho republisher
    }

    [Fact]
    public async Task Upload_NoActiveRubric_SkipsPublish_StaysUploaded()
    {
        using var t = new TestDb();
        var candidate = Guid.NewGuid();
        var session = TestDb.Session(candidate, SessionStatus.Ready);
        var q = TestDb.Question(session.Id);
        // KHÔNG seed rubric criterion -> không có gì để chấm.
        t.Db.AddRange(session, q);
        await t.Db.SaveChangesAsync();

        var publisher = new Mock<IScoringJobPublisher>();
        var svc = Build(t, publisher, out _);

        using var audio = new MemoryStream(new byte[] { 1 });
        var result = await svc.UploadAnswerAsync(session.Id, q.Id, candidate, audio, "audio/webm", 30);

        var saved = await t.Db.PracticeAnswers.AsNoTracking().FirstAsync(a => a.Id == result.AnswerId);
        Assert.Equal(AnswerStatus.Uploaded, saved.Status);
        Assert.Null(saved.LastScoringPublishedAt);
        publisher.Verify(p => p.PublishAsync(It.IsAny<ScoringJob>(), It.IsAny<CancellationToken>()), Times.Never);
    }

    [Fact]
    public async Task Upload_WrongCandidate_Throws()
    {
        using var t = new TestDb();
        var owner = Guid.NewGuid();
        var session = TestDb.Session(owner, SessionStatus.Ready);
        var q = TestDb.Question(session.Id);
        t.Db.AddRange(session, q);
        await t.Db.SaveChangesAsync();

        var svc = Build(t, new Mock<IScoringJobPublisher>(), out _);
        using var audio = new MemoryStream(new byte[] { 1 });

        await Assert.ThrowsAsync<UnauthorizedAccessException>(() =>
            svc.UploadAnswerAsync(session.Id, q.Id, Guid.NewGuid(), audio, "audio/webm", 30));
    }

    [Fact]
    public async Task Upload_CompletedSession_Throws()
    {
        using var t = new TestDb();
        var candidate = Guid.NewGuid();
        var session = TestDb.Session(candidate, SessionStatus.Scored);
        var q = TestDb.Question(session.Id);
        t.Db.AddRange(session, q);
        await t.Db.SaveChangesAsync();

        var svc = Build(t, new Mock<IScoringJobPublisher>(), out _);
        using var audio = new MemoryStream(new byte[] { 1 });

        await Assert.ThrowsAsync<InvalidOperationException>(() =>
            svc.UploadAnswerAsync(session.Id, q.Id, candidate, audio, "audio/webm", 30));
    }

    [Fact]
    public async Task SaveResult_SavesScores_StatusScored()
    {
        using var t = new TestDb();
        var candidate = Guid.NewGuid();
        var session = TestDb.Session(candidate, SessionStatus.Scoring);
        var q = TestDb.Question(session.Id);
        var crit = TestDb.Criterion(session.JobCategory);
        var answer = TestDb.Answer(session.Id, q.Id, AnswerStatus.Scoring, DateTime.UtcNow, DateTime.UtcNow);
        t.Db.AddRange(session, q, crit, answer);
        await t.Db.SaveChangesAsync();

        var svc = Build(t, new Mock<IScoringJobPublisher>(), out _);

        var req = new AnswerScoreCallbackRequest
        {
            Transcript = "Đây là câu trả lời",
            RubricVersion = 1,
            Scores = { new ScoreItemDto { CriterionId = crit.Id, Score = 4.5m, Reasoning = "ok" } }
        };
        await svc.SaveResultAsync(answer.Id, req);

        var saved = await t.Db.PracticeAnswers.AsNoTracking().Include(a => a.Scores)
            .FirstAsync(a => a.Id == answer.Id);
        Assert.Equal(AnswerStatus.Scored, saved.Status);
        Assert.Equal("Đây là câu trả lời", saved.Transcript);
        Assert.Single(saved.Scores);
        Assert.Equal(4.5m, saved.Scores.First().Score);

        // Answer cuối Scored -> session đóng sang Scored.
        var s = await t.Db.PracticeSessions.AsNoTracking().FirstAsync(x => x.Id == session.Id);
        Assert.Equal(SessionStatus.Scored, s.Status);
    }

    [Fact]
    public async Task SaveResult_CalledTwice_IsIdempotent_NoDuplicateScores()
    {
        using var t = new TestDb();
        var candidate = Guid.NewGuid();
        var session = TestDb.Session(candidate, SessionStatus.Scoring);
        var q = TestDb.Question(session.Id);
        var crit = TestDb.Criterion(session.JobCategory);
        var answer = TestDb.Answer(session.Id, q.Id, AnswerStatus.Scoring, DateTime.UtcNow, DateTime.UtcNow);
        t.Db.AddRange(session, q, crit, answer);
        await t.Db.SaveChangesAsync();

        var svc = Build(t, new Mock<IScoringJobPublisher>(), out _);
        var req = new AnswerScoreCallbackRequest
        {
            Transcript = "x",
            RubricVersion = 1,
            Scores = { new ScoreItemDto { CriterionId = crit.Id, Score = 3m, Reasoning = "a" } }
        };

        await svc.SaveResultAsync(answer.Id, req);
        await svc.SaveResultAsync(answer.Id, req);   // worker retry gửi lại

        var count = await t.Db.AnswerScores.AsNoTracking().CountAsync(s => s.AnswerId == answer.Id);
        Assert.Equal(1, count);   // không nhân đôi
    }

    [Fact]
    public async Task MarkFailed_SetsFailed_AndClosesSession_WhenScoring()
    {
        using var t = new TestDb();
        var candidate = Guid.NewGuid();
        var session = TestDb.Session(candidate, SessionStatus.Scoring);
        var q = TestDb.Question(session.Id);
        // Answer đang Scoring là answer cuối -> mark Failed phải đóng session sang Scored.
        var a = TestDb.Answer(session.Id, q.Id, AnswerStatus.Scoring, DateTime.UtcNow, DateTime.UtcNow);
        t.Db.AddRange(session, q, a);
        await t.Db.SaveChangesAsync();

        var svc = Build(t, new Mock<IScoringJobPublisher>(), out _);
        await svc.MarkFailedAsync(a.Id, "audio hỏng");

        var saved = await t.Db.PracticeAnswers.AsNoTracking().FirstAsync(x => x.Id == a.Id);
        Assert.Equal(AnswerStatus.Failed, saved.Status);

        var s = await t.Db.PracticeSessions.AsNoTracking().FirstAsync(x => x.Id == session.Id);
        Assert.Equal(SessionStatus.Scored, s.Status);   // Failed tính là "xong" -> đóng được
    }

    [Fact]
    public async Task MarkFailed_AlreadyScored_IsNoOp()
    {
        using var t = new TestDb();
        var candidate = Guid.NewGuid();
        var session = TestDb.Session(candidate, SessionStatus.Scoring);
        var q = TestDb.Question(session.Id);
        var a = TestDb.Answer(session.Id, q.Id, AnswerStatus.Scored, DateTime.UtcNow, DateTime.UtcNow);
        t.Db.AddRange(session, q, a);
        await t.Db.SaveChangesAsync();

        var svc = Build(t, new Mock<IScoringJobPublisher>(), out _);
        await svc.MarkFailedAsync(a.Id, "callback đến muộn");

        var saved = await t.Db.PracticeAnswers.AsNoTracking().FirstAsync(x => x.Id == a.Id);
        Assert.Equal(AnswerStatus.Scored, saved.Status);   // KHÔNG hạ Scored xuống Failed
    }

    [Fact]
    public async Task MarkFailed_UnknownAnswer_Throws()
    {
        using var t = new TestDb();
        var svc = Build(t, new Mock<IScoringJobPublisher>(), out _);
        await Assert.ThrowsAsync<KeyNotFoundException>(() =>
            svc.MarkFailedAsync(Guid.NewGuid(), "x"));
    }
}
