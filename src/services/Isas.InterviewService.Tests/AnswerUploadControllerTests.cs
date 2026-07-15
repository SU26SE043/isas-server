using System.Security.Claims;
using Isas.InterviewService.Controllers;
using Isas.InterviewService.Services.Interfaces;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Logging.Abstractions;
using Moq;

namespace Isas.InterviewService.Tests;

/// <summary>
/// P1-6 — AnswersController.Upload sớm-thoát: null/empty file → 400; sub claim không parse được → 401.
/// Hai nhánh này chưa có test. Kiểm cả việc AnswerService KHÔNG bị gọi khi input xấu.
/// </summary>
public class AnswerUploadControllerTests
{
    private static AnswersController Build(Mock<IAnswerService> service, ClaimsPrincipal? user = null)
    {
        var config = new ConfigurationBuilder().AddInMemoryCollection(new Dictionary<string, string?>
        {
            ["Internal:Token"] = "test-internal-token"
        }).Build();

        var controller = new AnswersController(service.Object, config, NullLogger<AnswersController>.Instance)
        {
            ControllerContext = new ControllerContext
            {
                HttpContext = new DefaultHttpContext
                {
                    User = user ?? new ClaimsPrincipal(new ClaimsIdentity())
                }
            }
        };
        return controller;
    }

    // Kiểm tra file đứng TRƯỚC kiểm tra sub → file null → 400 (chưa chạm claim/service).
    [Fact]
    public async Task Upload_NullFile_400_ServiceNotInvoked()
    {
        var service = new Mock<IAnswerService>();
        var controller = Build(service);

        var result = await controller.Upload(Guid.NewGuid(), Guid.NewGuid(), file: null!, durationSec: 30, default);

        Assert.IsType<BadRequestObjectResult>(result);
        VerifyNotUploaded(service);
    }

    [Fact]
    public async Task Upload_EmptyFile_400_ServiceNotInvoked()
    {
        var service = new Mock<IAnswerService>();
        var controller = Build(service);

        var file = new Mock<IFormFile>();
        file.Setup(f => f.Length).Returns(0);   // file rỗng

        var result = await controller.Upload(Guid.NewGuid(), Guid.NewGuid(), file.Object, durationSec: 30, default);

        Assert.IsType<BadRequestObjectResult>(result);
        VerifyNotUploaded(service);
    }

    // File hợp lệ NHƯNG không có sub/NameIdentifier trong token → không parse được candidateId → 401.
    [Fact]
    public async Task Upload_MissingSubClaim_401_ServiceNotInvoked()
    {
        var service = new Mock<IAnswerService>();
        var controller = Build(service);   // ClaimsIdentity rỗng → không có sub

        var file = new Mock<IFormFile>();
        file.Setup(f => f.Length).Returns(1024);

        var result = await controller.Upload(Guid.NewGuid(), Guid.NewGuid(), file.Object, durationSec: 30, default);

        Assert.IsType<UnauthorizedResult>(result);
        VerifyNotUploaded(service);
    }

    // Sub tồn tại nhưng KHÔNG phải GUID hợp lệ → cũng 401.
    [Fact]
    public async Task Upload_UnparsableSubClaim_401_ServiceNotInvoked()
    {
        var service = new Mock<IAnswerService>();
        var user = new ClaimsPrincipal(new ClaimsIdentity(new[]
        {
            new Claim(ClaimTypes.NameIdentifier, "not-a-guid")
        }));
        var controller = Build(service, user);

        var file = new Mock<IFormFile>();
        file.Setup(f => f.Length).Returns(1024);

        var result = await controller.Upload(Guid.NewGuid(), Guid.NewGuid(), file.Object, durationSec: 30, default);

        Assert.IsType<UnauthorizedResult>(result);
        VerifyNotUploaded(service);
    }

    private static void VerifyNotUploaded(Mock<IAnswerService> service) =>
        service.Verify(s => s.UploadAnswerAsync(
            It.IsAny<Guid>(), It.IsAny<Guid>(), It.IsAny<Guid>(),
            It.IsAny<Stream>(), It.IsAny<string>(), It.IsAny<int>(), It.IsAny<CancellationToken>()), Times.Never);
}
