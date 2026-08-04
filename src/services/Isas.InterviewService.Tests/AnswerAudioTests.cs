using Isas.InterviewService.Entities;
using Isas.InterviewService.Enums;
using Isas.InterviewService.Services;
using Isas.InterviewService.Services.Interfaces;
using System.Net;
using System.Security.Claims;
using Amazon.S3;
using Isas.InterviewService.Controllers;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc;
using Microsoft.Extensions.Logging.Abstractions;
using Moq;

namespace Isas.InterviewService.Tests;

public class AnswerAudioTests
{
    private static PracticeService Build(TestDb t, Mock<IStorageService> storage) =>
        new(t.Db, storage.Object, new Mock<IAiServiceQuestionGenerator>().Object,
            new Mock<ISessionScoringNotifier>().Object, new Mock<ICreditReservationClient>().Object,
            NullLogger<PracticeService>.Instance);

    private static PracticeController BuildController(Mock<IPracticeService> service, Guid candidateId) =>
        new(service.Object, new Mock<IQuestionSpeechService>().Object, NullLogger<PracticeController>.Instance)
        {
            ControllerContext = new ControllerContext
            {
                HttpContext = new DefaultHttpContext
                {
                    User = new ClaimsPrincipal(new ClaimsIdentity(
                        [new Claim(ClaimTypes.NameIdentifier, candidateId.ToString())], "Test"))
                }
            }
        };

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

    [Fact]
    public async Task AudioS3KhongCon_Tra404()
    {
        var candidate = Guid.NewGuid();
        var service = new Mock<IPracticeService>();
        service.Setup(s => s.GetAnswerAudioAsync(candidate, It.IsAny<Guid>(), It.IsAny<Guid>(), It.IsAny<CancellationToken>()))
            .ThrowsAsync(new AmazonS3Exception("NoSuchKey") { StatusCode = HttpStatusCode.NotFound });
        var controller = BuildController(service, candidate);

        var result = await controller.GetAnswerAudio(Guid.NewGuid(), Guid.NewGuid(), default);

        Assert.IsType<NotFoundObjectResult>(result);
    }

    [Fact]
    public async Task AudioS3LoiBatNgo_Tra500KhongNem()
    {
        var candidate = Guid.NewGuid();
        var service = new Mock<IPracticeService>();
        service.Setup(s => s.GetAnswerAudioAsync(candidate, It.IsAny<Guid>(), It.IsAny<Guid>(), It.IsAny<CancellationToken>()))
            .ThrowsAsync(new InvalidOperationException("S3 timeout"));
        var controller = BuildController(service, candidate);

        var result = await controller.GetAnswerAudio(Guid.NewGuid(), Guid.NewGuid(), default);

        var error = Assert.IsType<ObjectResult>(result);
        Assert.Equal(StatusCodes.Status500InternalServerError, error.StatusCode);
    }
}
