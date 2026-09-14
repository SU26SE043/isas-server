using Isas.InterviewService.ApplicationDbContext;
using Isas.InterviewService.Controllers;
using Isas.InterviewService.Entities;
using Isas.InterviewService.Enums;
using Isas.InterviewService.Services;
using Isas.InterviewService.Services.Interfaces;
using Microsoft.AspNetCore.Mvc;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Logging.Abstractions;
using Moq;
using System.Text.Json;

namespace Isas.InterviewService.Tests;

/// <summary>
/// E11c — HR (CampaignService) nghe bản ghi âm câu trả lời B2B qua đường máy-máy
/// <c>GET /internal/sessions/{sid}/answers/{aid}/audio</c>. Khác đường owner (AnswerAudioTests): KHÔNG check chủ
/// session — Campaign đã gate org + ranking. Token sai → 401; answer lạ / chưa có audio → 404; MIME suy từ đuôi key.
/// Kèm: <c>AnswerResponse.RejectReason</c> đi ra JSON để HR phân biệt "im lặng" (CAMP-21) với "bỏ trống".
/// </summary>
public class AnswerAudioInternalE11cTests
{
    private const string Token = "test-internal-token";

    private static PracticeService BuildPractice(InterviewDbContext db, Mock<IStorageService>? storage = null) =>
        new(db, (storage ?? new Mock<IStorageService>()).Object,
            new Mock<IAiServiceQuestionGenerator>().Object,
            new Mock<ISessionScoringNotifier>().Object,
            new Mock<ICreditReservationClient>().Object,
            NullLogger<PracticeService>.Instance);

    private static InternalSessionsController BuildController(TestDb t, Mock<IStorageService>? storage = null)
    {
        var config = new ConfigurationBuilder().AddInMemoryCollection(new Dictionary<string, string?>
        {
            ["Internal:Token"] = Token
        }).Build();
        return new InternalSessionsController(
            BuildPractice(t.NewContext(), storage), config, NullLogger<InternalSessionsController>.Instance);
    }

    private static (PracticeSession session, PracticeAnswer answer) Seed(TestDb t, string? audioKey, string? rejectReason = null)
    {
        var session = TestDb.Session(Guid.NewGuid(), SessionStatus.Scored, campaignId: Guid.NewGuid());
        var q = TestDb.Question(session.Id);
        var answer = TestDb.Answer(session.Id, q.Id, AnswerStatus.Scored, DateTime.UtcNow, DateTime.UtcNow,
            audioObjectKey: audioKey, rejectReason: rejectReason);
        using var seed = t.NewContext();
        seed.AddRange(session, q, answer);
        seed.SaveChanges();
        return (session, answer);
    }

    [Fact]
    public async Task Internal_KhongCheckChu_TraAudioVaMimeTheoDuoiKey()
    {
        using var t = new TestDb();
        var (session, answer) = Seed(t, "answer-audio/iphone.m4a");
        var storage = new Mock<IStorageService>();
        storage.Setup(s => s.DownloadAsync(answer.AudioObjectKey!, It.IsAny<CancellationToken>()))
            .ReturnsAsync(new MemoryStream([1, 2, 3]));

        // Gọi KHÔNG có candidateId — chứng minh đường internal không đòi chủ session.
        var result = await BuildPractice(t.NewContext(), storage).GetAnswerAudioInternalAsync(session.Id, answer.Id);

        Assert.NotNull(result);
        Assert.Equal("audio/mp4", result!.ContentType);   // m4a → audio/mp4, KHÔNG phải "audio/webm" cứng
        storage.Verify(s => s.DownloadAsync(answer.AudioObjectKey!, It.IsAny<CancellationToken>()), Times.Once);
    }

    [Fact]
    public async Task Internal_AnswerKhongThuocSession_TraNull_KhongDocStorage()
    {
        using var t = new TestDb();
        var (_, answer) = Seed(t, "answer-audio/a.webm");
        var storage = new Mock<IStorageService>();

        var result = await BuildPractice(t.NewContext(), storage).GetAnswerAudioInternalAsync(Guid.NewGuid(), answer.Id);

        Assert.Null(result);
        storage.Verify(s => s.DownloadAsync(It.IsAny<string>(), It.IsAny<CancellationToken>()), Times.Never);
    }

    [Fact]
    public async Task Internal_AnswerChuaCoAudio_TraNull()
    {
        using var t = new TestDb();
        var (session, answer) = Seed(t, audioKey: null);
        var result = await BuildPractice(t.NewContext()).GetAnswerAudioInternalAsync(session.Id, answer.Id);
        Assert.Null(result);
    }

    [Fact]
    public async Task Endpoint_TokenSai_401_KhongDocStorage()
    {
        using var t = new TestDb();
        var (session, answer) = Seed(t, "answer-audio/a.webm");
        var storage = new Mock<IStorageService>();

        var result = await BuildController(t, storage).GetSessionAnswerAudio(session.Id, answer.Id, "wrong", default);

        Assert.IsType<UnauthorizedObjectResult>(result);
        storage.Verify(s => s.DownloadAsync(It.IsAny<string>(), It.IsAny<CancellationToken>()), Times.Never);
    }

    [Fact]
    public async Task Endpoint_AnswerLa_404()
    {
        using var t = new TestDb();
        var (session, _) = Seed(t, "answer-audio/a.webm");
        var result = await BuildController(t).GetSessionAnswerAudio(session.Id, Guid.NewGuid(), Token, default);
        Assert.IsType<NotFoundObjectResult>(result);
    }

    [Fact]
    public async Task Endpoint_TokenDung_TraFileVoiContentType()
    {
        using var t = new TestDb();
        var (session, answer) = Seed(t, "answer-audio/a.webm");
        var storage = new Mock<IStorageService>();
        storage.Setup(s => s.DownloadAsync(answer.AudioObjectKey!, It.IsAny<CancellationToken>()))
            .ReturnsAsync(new MemoryStream([9, 9]));

        var result = await BuildController(t, storage).GetSessionAnswerAudio(session.Id, answer.Id, Token, default);

        var file = Assert.IsType<FileStreamResult>(result);
        Assert.Equal("audio/webm", file.ContentType);
        Assert.True(file.EnableRangeProcessing);
    }

    [Fact]
    public async Task AnswerResponse_MangRejectReason_RaJsonCamelCase()
    {
        using var t = new TestDb();
        var (session, _) = Seed(t, "answer-audio/silent.webm", rejectReason: "no_speech");

        var questions = await BuildPractice(t.NewContext()).GetSessionAnswersInternalAsync(session.Id);

        var answer = Assert.Single(questions!).Answer;
        Assert.NotNull(answer);
        Assert.Equal("no_speech", answer!.RejectReason);

        // Khoá JSON — Campaign đọc "rejectReason" (camelCase). Đổi tên là field rụng im lặng ở phía đọc.
        var json = JsonSerializer.Serialize(answer, new JsonSerializerOptions(JsonSerializerDefaults.Web));
        Assert.Contains("\"rejectReason\":\"no_speech\"", json);
    }

    [Fact]
    public async Task AnswerResponse_KhongCoLyDo_RejectReasonNull()
    {
        using var t = new TestDb();
        var (session, _) = Seed(t, "answer-audio/ok.webm");
        var questions = await BuildPractice(t.NewContext()).GetSessionAnswersInternalAsync(session.Id);
        Assert.Null(Assert.Single(questions!).Answer!.RejectReason);
    }
}
