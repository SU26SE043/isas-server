using Isas.InterviewService.Controllers;
using Isas.InterviewService.DTOs;
using Isas.InterviewService.Services.Interfaces;
using Microsoft.AspNetCore.Mvc;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Logging.Abstractions;
using Moq;

namespace Isas.InterviewService.Tests;

/// <summary>
/// P1-4 — hai callback INTERNAL chấm điểm (POST /internal/answers/{id}/result và /failed) là
/// [AllowAnonymous], chỉ chặn bằng X-Internal-Token (IsValidInternalToken). Guard này chưa có test.
/// Kiểm: thiếu / rỗng / sai token → 401 VÀ service KHÔNG bị gọi; token đúng → 204 + service được gọi;
/// token cấu hình rỗng → fail-closed 401. Mẫu theo CampaignSessionTests.InternalController_SaiToken_Tra401.
/// </summary>
public class AnswerCallbackAuthTests
{
    private const string ExpectedToken = "test-internal-token";

    private static AnswersController Build(Mock<IAnswerService> service, string? configuredToken = ExpectedToken)
    {
        var config = new ConfigurationBuilder().AddInMemoryCollection(new Dictionary<string, string?>
        {
            ["Internal:Token"] = configuredToken
        }).Build();

        return new AnswersController(service.Object, config, NullLogger<AnswersController>.Instance);
    }

    private static AnswerScoreCallbackRequest ScoreReq() => new()
    {
        Transcript = "câu trả lời",
        RubricVersion = 1,
        Scores = new List<ScoreItemDto> { new() { CriterionId = Guid.NewGuid(), Score = 4 } }
    };

    // ── /result ─────────────────────────────────────────────────────────────

    [Theory]
    [InlineData(null)]        // thiếu header
    [InlineData("")]          // rỗng
    [InlineData("wrong")]     // sai
    public async Task SaveResult_BadToken_401_ServiceNotInvoked(string? token)
    {
        var service = new Mock<IAnswerService>();
        var controller = Build(service);

        var result = await controller.SaveResult(Guid.NewGuid(), ScoreReq(), token, default);

        Assert.IsType<UnauthorizedObjectResult>(result);
        service.Verify(s => s.SaveResultAsync(
            It.IsAny<Guid>(), It.IsAny<AnswerScoreCallbackRequest>(), It.IsAny<CancellationToken>()), Times.Never);
    }

    [Fact]
    public async Task SaveResult_ValidToken_204_ServiceInvoked()
    {
        var answerId = Guid.NewGuid();
        var service = new Mock<IAnswerService>();
        service.Setup(s => s.SaveResultAsync(answerId, It.IsAny<AnswerScoreCallbackRequest>(), It.IsAny<CancellationToken>()))
               .Returns(Task.CompletedTask);
        var controller = Build(service);

        var result = await controller.SaveResult(answerId, ScoreReq(), ExpectedToken, default);

        Assert.IsType<NoContentResult>(result);
        service.Verify(s => s.SaveResultAsync(answerId, It.IsAny<AnswerScoreCallbackRequest>(), It.IsAny<CancellationToken>()),
            Times.Once);
    }

    // Fail-closed: token kỳ vọng cấu hình RỖNG → mọi callback (kể cả gửi rỗng) → 401 (không mở toang).
    [Fact]
    public async Task SaveResult_ConfiguredTokenEmpty_FailsClosed_401()
    {
        var service = new Mock<IAnswerService>();
        var controller = Build(service, configuredToken: "");

        var result = await controller.SaveResult(Guid.NewGuid(), ScoreReq(), token: "", default);

        Assert.IsType<UnauthorizedObjectResult>(result);
        service.Verify(s => s.SaveResultAsync(
            It.IsAny<Guid>(), It.IsAny<AnswerScoreCallbackRequest>(), It.IsAny<CancellationToken>()), Times.Never);
    }

    // ── /failed ─────────────────────────────────────────────────────────────

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("wrong")]
    public async Task MarkFailed_BadToken_401_ServiceNotInvoked(string? token)
    {
        var service = new Mock<IAnswerService>();
        var controller = Build(service);

        var req = new AnswerFailedCallbackRequest { Reason = "audio hỏng" };
        var result = await controller.MarkFailed(Guid.NewGuid(), req, token, default);

        Assert.IsType<UnauthorizedObjectResult>(result);
        service.Verify(s => s.MarkFailedAsync(
            It.IsAny<Guid>(), It.IsAny<string?>(), It.IsAny<bool>(), It.IsAny<CancellationToken>()), Times.Never);
    }

    [Fact]
    public async Task MarkFailed_ValidToken_204_ServiceInvoked()
    {
        var answerId = Guid.NewGuid();
        var service = new Mock<IAnswerService>();
        service.Setup(s => s.MarkFailedAsync(answerId, It.IsAny<string?>(), It.IsAny<bool>(), It.IsAny<CancellationToken>()))
               .Returns(Task.CompletedTask);
        var controller = Build(service);

        var req = new AnswerFailedCallbackRequest { Reason = "audio hỏng" };
        var result = await controller.MarkFailed(answerId, req, ExpectedToken, default);

        Assert.IsType<NoContentResult>(result);
        service.Verify(s => s.MarkFailedAsync(answerId, "audio hỏng", false, It.IsAny<CancellationToken>()), Times.Once);
    }

    [Fact]
    public async Task MarkFailed_ConfiguredTokenEmpty_FailsClosed_401()
    {
        var service = new Mock<IAnswerService>();
        var controller = Build(service, configuredToken: "");

        var req = new AnswerFailedCallbackRequest { Reason = "audio hỏng" };
        var result = await controller.MarkFailed(Guid.NewGuid(), req, token: "", default);

        Assert.IsType<UnauthorizedObjectResult>(result);
        service.Verify(s => s.MarkFailedAsync(
            It.IsAny<Guid>(), It.IsAny<string?>(), It.IsAny<bool>(), It.IsAny<CancellationToken>()), Times.Never);
    }
}
