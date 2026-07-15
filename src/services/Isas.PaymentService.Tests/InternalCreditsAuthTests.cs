using Isas.PaymentService.Controllers;
using Isas.PaymentService.DTOs;
using Isas.PaymentService.Services;
using Microsoft.AspNetCore.Mvc;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Logging.Abstractions;
using Moq;
using PaymentService.Models;

namespace Isas.PaymentService.Tests;

/// <summary>
/// COMMIT-4 — /internal/credits/* là ranh giới auth DUY NHẤT cho ghi tiền (reserve/consume/release),
/// [AllowAnonymous] + chỉ chặn bằng X-Internal-Token (IsValidInternalToken). Sau khi đổi sang so khớp
/// hằng-thời-gian (CryptographicOperations.FixedTimeEquals), hành vi vẫn PHẢI giữ: thiếu/rỗng/sai →
/// 401 + service KHÔNG chạy; đúng → xử lý; token cấu hình rỗng → fail-closed 401.
/// </summary>
public class InternalCreditsAuthTests
{
    private const string ExpectedToken = "test-internal-token";

    private static InternalCreditsController Build(Mock<ICreditAccountService> credits, string? configuredToken = ExpectedToken)
    {
        var config = new ConfigurationBuilder().AddInMemoryCollection(new Dictionary<string, string?>
        {
            ["Internal:Token"] = configuredToken
        }).Build();

        return new InternalCreditsController(credits.Object, config, NullLogger<InternalCreditsController>.Instance);
    }

    private static CreditOpRequest Req() => new()
    {
        OwnerType = OwnerType.User,
        OwnerId = Guid.NewGuid(),
        SessionId = Guid.NewGuid()
    };

    [Theory]
    [InlineData(null)]        // thiếu header
    [InlineData("")]          // rỗng
    [InlineData("wrong")]     // sai
    [InlineData("test-internal-toke")]   // đúng prefix, khác 1 ký tự cuối (dò timing)
    public async Task Reserve_BadToken_401_ServiceNotInvoked(string? token)
    {
        var credits = new Mock<ICreditAccountService>();
        var controller = Build(credits);

        var result = await controller.ReserveAsync(Req(), token, default);

        Assert.IsType<UnauthorizedObjectResult>(result);
        credits.Verify(c => c.ReserveAsync(
            It.IsAny<OwnerType>(), It.IsAny<Guid>(), It.IsAny<Guid>(), It.IsAny<CancellationToken>()), Times.Never);
    }

    [Fact]
    public async Task Reserve_ValidToken_InvokesService()
    {
        var credits = new Mock<ICreditAccountService>();
        credits
            .Setup(c => c.ReserveAsync(It.IsAny<OwnerType>(), It.IsAny<Guid>(), It.IsAny<Guid>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(ReserveResult.Reserved(Guid.NewGuid(), 1));
        var controller = Build(credits);

        var result = await controller.ReserveAsync(Req(), ExpectedToken, default);

        Assert.IsType<OkObjectResult>(result);
        credits.Verify(c => c.ReserveAsync(
            It.IsAny<OwnerType>(), It.IsAny<Guid>(), It.IsAny<Guid>(), It.IsAny<CancellationToken>()), Times.Once);
    }

    // Fail-closed: token cấu hình RỖNG → mọi request (kể cả gửi rỗng) → 401 (không mở toang).
    [Fact]
    public async Task Reserve_ConfiguredTokenEmpty_FailsClosed_401()
    {
        var credits = new Mock<ICreditAccountService>();
        var controller = Build(credits, configuredToken: "");

        var result = await controller.ReserveAsync(Req(), token: "", default);

        Assert.IsType<UnauthorizedObjectResult>(result);
        credits.Verify(c => c.ReserveAsync(
            It.IsAny<OwnerType>(), It.IsAny<Guid>(), It.IsAny<Guid>(), It.IsAny<CancellationToken>()), Times.Never);
    }
}
