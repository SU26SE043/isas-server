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

/// <summary>B2C coaching (2026-09-17) — ánh xạ exception → status code của PracticeFaceCheckController.</summary>
public class PracticeFaceCheckControllerTests
{
    private static readonly Guid CandidateId = Guid.NewGuid();

    private static PracticeFaceCheckController Build(Mock<IPracticeFaceCheckService> service)
    {
        var user = new ClaimsPrincipal(new ClaimsIdentity(new[]
        {
            new Claim(ClaimTypes.NameIdentifier, CandidateId.ToString())
        }));

        return new PracticeFaceCheckController(service.Object, NullLogger<PracticeFaceCheckController>.Instance)
        {
            ControllerContext = new ControllerContext
            {
                HttpContext = new DefaultHttpContext { User = user }
            }
        };
    }

    private static Mock<IFormFile> FakeImage()
    {
        var file = new Mock<IFormFile>();
        file.Setup(f => f.Length).Returns(6);
        file.Setup(f => f.OpenReadStream()).Returns(() => new MemoryStream(new byte[] { 0xFF, 0xD8, 0xFF, 0, 0, 0 }));
        return file;
    }

    [Fact]
    public async Task KetQua_KhacNull_Tra200()
    {
        var service = new Mock<IPracticeFaceCheckService>();
        service.Setup(s => s.RecordFaceCheckAsync(
                CandidateId, It.IsAny<Guid>(), It.IsAny<Stream>(), It.IsAny<long>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(new FaceCheckResultResponse(1, Array.Empty<string>()));

        var result = await Build(service).RecordFaceCheck(Guid.NewGuid(), FakeImage().Object, default);

        var ok = Assert.IsType<OkObjectResult>(result);
        Assert.IsType<FaceCheckResultResponse>(ok.Value);
    }

    [Fact]
    public async Task KetQuaNull_Tra204()
    {
        var service = new Mock<IPracticeFaceCheckService>();
        service.Setup(s => s.RecordFaceCheckAsync(
                CandidateId, It.IsAny<Guid>(), It.IsAny<Stream>(), It.IsAny<long>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync((FaceCheckResultResponse?)null);

        var result = await Build(service).RecordFaceCheck(Guid.NewGuid(), FakeImage().Object, default);

        Assert.IsType<NoContentResult>(result);
    }

    [Fact]
    public async Task ThieuAnh_Tra400_KhongGoiService()
    {
        var service = new Mock<IPracticeFaceCheckService>();
        var result = await Build(service).RecordFaceCheck(Guid.NewGuid(), null!, default);

        Assert.IsType<BadRequestObjectResult>(result);
        service.Verify(s => s.RecordFaceCheckAsync(
            It.IsAny<Guid>(), It.IsAny<Guid>(), It.IsAny<Stream>(), It.IsAny<long>(), It.IsAny<CancellationToken>()), Times.Never);
    }

    [Fact]
    public async Task KeyNotFound_Tra404()
    {
        var service = new Mock<IPracticeFaceCheckService>();
        service.Setup(s => s.RecordFaceCheckAsync(
                It.IsAny<Guid>(), It.IsAny<Guid>(), It.IsAny<Stream>(), It.IsAny<long>(), It.IsAny<CancellationToken>()))
            .ThrowsAsync(new KeyNotFoundException("Session không tồn tại"));

        var result = await Build(service).RecordFaceCheck(Guid.NewGuid(), FakeImage().Object, default);

        var notFound = Assert.IsType<NotFoundObjectResult>(result);
        Assert.Equal(StatusCodes.Status404NotFound, notFound.StatusCode);
    }

    [Fact]
    public async Task Unauthorized_Tra403()
    {
        var service = new Mock<IPracticeFaceCheckService>();
        service.Setup(s => s.RecordFaceCheckAsync(
                It.IsAny<Guid>(), It.IsAny<Guid>(), It.IsAny<Stream>(), It.IsAny<long>(), It.IsAny<CancellationToken>()))
            .ThrowsAsync(new UnauthorizedAccessException("Không phải buổi của bạn"));

        var result = await Build(service).RecordFaceCheck(Guid.NewGuid(), FakeImage().Object, default);

        var forbid = Assert.IsType<ObjectResult>(result);
        Assert.Equal(StatusCodes.Status403Forbidden, forbid.StatusCode);
    }

    [Fact]
    public async Task InvalidOperation_Tra400()
    {
        var service = new Mock<IPracticeFaceCheckService>();
        service.Setup(s => s.RecordFaceCheckAsync(
                It.IsAny<Guid>(), It.IsAny<Guid>(), It.IsAny<Stream>(), It.IsAny<long>(), It.IsAny<CancellationToken>()))
            .ThrowsAsync(new InvalidOperationException("Ảnh không phải định dạng JPEG."));

        var result = await Build(service).RecordFaceCheck(Guid.NewGuid(), FakeImage().Object, default);

        Assert.IsType<BadRequestObjectResult>(result);
    }

    [Fact]
    public async Task AiServiceException_KhongTimeout_Tra502()
    {
        var service = new Mock<IPracticeFaceCheckService>();
        service.Setup(s => s.RecordFaceCheckAsync(
                It.IsAny<Guid>(), It.IsAny<Guid>(), It.IsAny<Stream>(), It.IsAny<long>(), It.IsAny<CancellationToken>()))
            .ThrowsAsync(new AiServiceException("AIService /face-detect trả 500"));

        var result = await Build(service).RecordFaceCheck(Guid.NewGuid(), FakeImage().Object, default);

        var obj = Assert.IsType<ObjectResult>(result);
        Assert.Equal(StatusCodes.Status502BadGateway, obj.StatusCode);
    }

    [Fact]
    public async Task AiServiceException_Timeout_Tra504()
    {
        var service = new Mock<IPracticeFaceCheckService>();
        service.Setup(s => s.RecordFaceCheckAsync(
                It.IsAny<Guid>(), It.IsAny<Guid>(), It.IsAny<Stream>(), It.IsAny<long>(), It.IsAny<CancellationToken>()))
            .ThrowsAsync(new AiServiceException("hết giờ") { IsTimeout = true });

        var result = await Build(service).RecordFaceCheck(Guid.NewGuid(), FakeImage().Object, default);

        var obj = Assert.IsType<ObjectResult>(result);
        Assert.Equal(StatusCodes.Status504GatewayTimeout, obj.StatusCode);
    }
}
