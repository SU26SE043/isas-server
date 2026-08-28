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
/// MIS1-B8 — /start và /retry ném <see cref="AiServiceException"/> (Gemini 503/timeout) đang thoát ra
/// ngoài dạng 500 dù cả hai action khai `[ProducesResponseType(StatusCodes.Status502BadGateway)]` —
/// thiếu khối catch trong khi 2 action anh em (Create, OpenLesson) đã có. Khoá cả hai action mới +
/// đối chứng các nhánh lỗi lân cận (InvalidOperationException, InsufficientCreditException) không bị
/// khối catch mới nuốt nhầm hoặc chen sai thứ tự.
/// </summary>
public class RoadmapsControllerErrorMappingTests
{
    private static RoadmapsController Build(Mock<IRoadmapLessonService> lessonService, Guid candidateId)
    {
        var user = new ClaimsPrincipal(new ClaimsIdentity(new[]
        {
            new Claim(ClaimTypes.NameIdentifier, candidateId.ToString())
        }, "Test"));

        return new RoadmapsController(
            new Mock<IRoadmapService>().Object,
            lessonService.Object,
            new Mock<IRoadmapReportService>().Object,
            NullLogger<RoadmapsController>.Instance)
        {
            ControllerContext = new ControllerContext
            {
                HttpContext = new DefaultHttpContext { User = user }
            }
        };
    }

    // T1 — StartLesson: AiServiceException KHÔNG được thoát ra ngoài dạng 500 unhandled, phải map 502.
    [Fact]
    public async Task StartLesson_AiServiceException_Returns502()
    {
        var candidate = Guid.NewGuid();
        var roadmapId = Guid.NewGuid();
        var lessonId = Guid.NewGuid();
        var lessonService = new Mock<IRoadmapLessonService>();
        lessonService
            .Setup(s => s.StartLessonAsync(candidate, roadmapId, lessonId, It.IsAny<CancellationToken>()))
            .ThrowsAsync(new AiServiceException("AIService /generate-questions trả 503"));
        var controller = Build(lessonService, candidate);

        var result = await controller.StartLesson(roadmapId, lessonId, default);

        var obj = Assert.IsType<ObjectResult>(result);
        Assert.Equal(StatusCodes.Status502BadGateway, obj.StatusCode);
    }

    // T2 — RetryLesson: cùng ca, cùng kỳ vọng 502.
    [Fact]
    public async Task RetryLesson_AiServiceException_Returns502()
    {
        var candidate = Guid.NewGuid();
        var roadmapId = Guid.NewGuid();
        var lessonId = Guid.NewGuid();
        var lessonService = new Mock<IRoadmapLessonService>();
        lessonService
            .Setup(s => s.RetryLessonAsync(candidate, roadmapId, lessonId, It.IsAny<CancellationToken>()))
            .ThrowsAsync(new AiServiceException("AIService /generate-questions timeout"));
        var controller = Build(lessonService, candidate);

        var result = await controller.RetryLesson(roadmapId, lessonId, default);

        var obj = Assert.IsType<ObjectResult>(result);
        Assert.Equal(StatusCodes.Status502BadGateway, obj.StatusCode);
    }

    // T3 — đối chứng: InvalidOperationException (AI trả rỗng / CV-JD không đọc được) vẫn 400,
    // KHÔNG bị khối AiServiceException mới nuốt.
    [Fact]
    public async Task StartLesson_InvalidOperation_Returns400()
    {
        var candidate = Guid.NewGuid();
        var roadmapId = Guid.NewGuid();
        var lessonId = Guid.NewGuid();
        var lessonService = new Mock<IRoadmapLessonService>();
        lessonService
            .Setup(s => s.StartLessonAsync(candidate, roadmapId, lessonId, It.IsAny<CancellationToken>()))
            .ThrowsAsync(new InvalidOperationException("AIService không trả về câu hỏi nào"));
        var controller = Build(lessonService, candidate);

        var result = await controller.StartLesson(roadmapId, lessonId, default);

        Assert.IsType<BadRequestObjectResult>(result);
    }

    [Fact]
    public async Task RetryLesson_InvalidOperation_Returns400()
    {
        var candidate = Guid.NewGuid();
        var roadmapId = Guid.NewGuid();
        var lessonId = Guid.NewGuid();
        var lessonService = new Mock<IRoadmapLessonService>();
        lessonService
            .Setup(s => s.RetryLessonAsync(candidate, roadmapId, lessonId, It.IsAny<CancellationToken>()))
            .ThrowsAsync(new InvalidOperationException("AIService không trả về câu hỏi nào"));
        var controller = Build(lessonService, candidate);

        var result = await controller.RetryLesson(roadmapId, lessonId, default);

        Assert.IsType<BadRequestObjectResult>(result);
    }

    // T4 — đối chứng: InsufficientCreditException vẫn 402 — khối AiServiceException mới đặt SAU nó
    // (không chen trước) nên không thể cướp nhánh này.
    [Fact]
    public async Task StartLesson_InsufficientCredit_Returns402()
    {
        var candidate = Guid.NewGuid();
        var roadmapId = Guid.NewGuid();
        var lessonId = Guid.NewGuid();
        var lessonService = new Mock<IRoadmapLessonService>();
        lessonService
            .Setup(s => s.StartLessonAsync(candidate, roadmapId, lessonId, It.IsAny<CancellationToken>()))
            .ThrowsAsync(new InsufficientCreditException("Không đủ credit"));
        var controller = Build(lessonService, candidate);

        var result = await controller.StartLesson(roadmapId, lessonId, default);

        var obj = Assert.IsType<ObjectResult>(result);
        Assert.Equal(StatusCodes.Status402PaymentRequired, obj.StatusCode);
    }

    [Fact]
    public async Task RetryLesson_InsufficientCredit_Returns402()
    {
        var candidate = Guid.NewGuid();
        var roadmapId = Guid.NewGuid();
        var lessonId = Guid.NewGuid();
        var lessonService = new Mock<IRoadmapLessonService>();
        lessonService
            .Setup(s => s.RetryLessonAsync(candidate, roadmapId, lessonId, It.IsAny<CancellationToken>()))
            .ThrowsAsync(new InsufficientCreditException("Không đủ credit"));
        var controller = Build(lessonService, candidate);

        var result = await controller.RetryLesson(roadmapId, lessonId, default);

        var obj = Assert.IsType<ObjectResult>(result);
        Assert.Equal(StatusCodes.Status402PaymentRequired, obj.StatusCode);
    }
}
