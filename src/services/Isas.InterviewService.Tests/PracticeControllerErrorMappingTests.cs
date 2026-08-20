using System.Security.Claims;
using Isas.InterviewService.Controllers;
using Isas.InterviewService.DTOs;
using Isas.InterviewService.Services;
using Isas.InterviewService.Services.Interfaces;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc;
using Microsoft.Extensions.Logging.Abstractions;
using Moq;

namespace Isas.InterviewService.Tests;

/// <summary>
/// COMMIT-3 — PracticeController.CreateSession phân biệt mã lỗi: AIService lỗi thật
/// (AiServiceException = transport/timeout/5xx khi sinh câu hỏi) → 502 (upstream), KHÔNG nuốt thành 400.
/// InvalidOperationException (AI trả rỗng / CV-JD không đọc được) giữ 400. Verify path 502
/// ProducesResponseType thật sự trigger.
/// </summary>
public class PracticeControllerErrorMappingTests
{
    private static PracticeController Build(Mock<IPracticeService> service, Guid candidateId)
    {
        var user = new ClaimsPrincipal(new ClaimsIdentity(new[]
        {
            new Claim(ClaimTypes.NameIdentifier, candidateId.ToString())
        }, "Test"));

        // TTS đọc câu hỏi: controller nhận thêm IQuestionSpeechService — test này không chạm
        // endpoint /speech nên mock trần là đủ.
        return new PracticeController(service.Object, new Mock<IQuestionSpeechService>().Object,
            NullLogger<PracticeController>.Instance)
        {
            ControllerContext = new ControllerContext
            {
                HttpContext = new DefaultHttpContext { User = user }
            }
        };
    }

    private static CreatePracticeSessionRequest Req() =>
        new(null, null, Isas.InterviewService.Enums.JobCategory.BE);

    [Fact]
    public async Task CreateSession_AiServiceException_Returns502()
    {
        var candidate = Guid.NewGuid();
        var service = new Mock<IPracticeService>();
        service
            .Setup(s => s.CreateSessionAsync(candidate, It.IsAny<CreatePracticeSessionRequest>(), It.IsAny<CancellationToken>()))
            .ThrowsAsync(new AiServiceException("AIService /generate-questions trả 503"));
        var controller = Build(service, candidate);

        var result = await controller.CreateSession(Req(), default);

        var obj = Assert.IsType<ObjectResult>(result);
        Assert.Equal(StatusCodes.Status502BadGateway, obj.StatusCode);
    }

    // Regression: AI trả rỗng / CV-JD không đọc được (InvalidOperationException) VẪN là 400 (lỗi input,
    // không phải upstream) — không bị AiServiceException-branch nuốt.
    [Fact]
    public async Task CreateSession_InvalidOperation_Returns400()
    {
        var candidate = Guid.NewGuid();
        var service = new Mock<IPracticeService>();
        service
            .Setup(s => s.CreateSessionAsync(candidate, It.IsAny<CreatePracticeSessionRequest>(), It.IsAny<CancellationToken>()))
            .ThrowsAsync(new InvalidOperationException("AIService không trả về câu hỏi nào"));
        var controller = Build(service, candidate);

        var result = await controller.CreateSession(Req(), default);

        Assert.IsType<BadRequestObjectResult>(result);
    }

    [Fact]
    public async Task SessionOptions_DelegatesCandidateAndJobCategory()
    {
        var candidate = Guid.NewGuid();
        var service = new Mock<IPracticeService>();
        service.Setup(s => s.GetSessionOptionsAsync(candidate, "BE", null, It.IsAny<CancellationToken>()))
            .ReturnsAsync(new PracticeSessionOptionsResponse(false, 0, 0, 1, 20, 12, [], [], 0, 0));
        var controller = Build(service, candidate);

        var result = await controller.GetSessionOptions("BE", null, default);

        Assert.IsType<OkObjectResult>(result);
        service.Verify(s => s.GetSessionOptionsAsync(candidate, "BE", null, It.IsAny<CancellationToken>()), Times.Once);
    }

    // Controller phải CHUYỂN TIẾP `language` chứ không nuốt: preview dựng trên rubric khác ngôn ngữ với
    // buổi thật sẽ ra số câu gốc khác (số tiêu chí nội dung là SÀN của số câu gốc).
    [Fact]
    public async Task SessionOptions_ChuyenTiepLanguage()
    {
        var candidate = Guid.NewGuid();
        var service = new Mock<IPracticeService>();
        service.Setup(s => s.GetSessionOptionsAsync(candidate, "BE", "en", It.IsAny<CancellationToken>()))
            .ReturnsAsync(new PracticeSessionOptionsResponse(true, 3, 3, 1, 20, 20, [], [], 1, 3));
        var controller = Build(service, candidate);

        var result = await controller.GetSessionOptions("BE", "en", default);

        Assert.IsType<OkObjectResult>(result);
        service.Verify(s => s.GetSessionOptionsAsync(candidate, "BE", "en", It.IsAny<CancellationToken>()), Times.Once);
    }
}
