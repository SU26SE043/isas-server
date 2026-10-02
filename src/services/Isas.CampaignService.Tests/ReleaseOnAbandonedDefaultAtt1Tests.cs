using System.Runtime.CompilerServices;
using Isas.CampaignService.Services;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging.Abstractions;
using Moq;
using RabbitMQ.Client;

namespace Isas.CampaignService.Tests;

/// <summary>
/// ATT1 — khoá GIÁ TRỊ MẶC ĐỊNH của <c>Membership:ReleaseOnAbandoned</c> trong appsettings.json ĐANG SHIP.
///
/// Vì sao cần: cờ này tắt từ 05/08 nên Campaign chưa từng bind <c>session.abandoned</c> ⇒ membership của
/// buổi bị huỷ nằm <c>InProgress</c> mãi (đo trên dev khi chạy L3 ATT1): <c>lastAttemptAbandoned</c>
/// luôn false, và buổi đã chết vẫn bị đếm là "đang thi" vào <c>MaxConcurrentInterviews</c>, số người
/// đang thi theo khung giờ, guard xoá khung giờ và thống kê. Mọi test khác tự dựng config tường minh
/// (<see cref="SessionScoredConsumerTests"/>), nên lật mặc định về false KHÔNG làm test nào đỏ — mà mặc
/// định chính là thứ có hiệu lực trên deploy (không env nào đặt cờ này).
/// </summary>
public class ReleaseOnAbandonedDefaultAtt1Tests
{
    [Fact]
    public async Task AppsettingsDangShip_BatReleaseOnAbandoned_ConsumerBindSessionAbandoned()
    {
        var config = new ConfigurationBuilder()
            .AddJsonFile(Path.Combine(RepoRoot(), "src", "services", "Isas.CampaignService", "appsettings.json"),
                optional: false)
            .Build();

        Assert.True(config.GetValue<bool>("Membership:ReleaseOnAbandoned"));

        // Đi trọn đường từ config đang ship tới topology: consumer dựng từ chính file đó phải bind abandoned.
        var consumer = new SessionScoredConsumer(
            config,
            new ServiceCollection().BuildServiceProvider().GetRequiredService<IServiceScopeFactory>(),
            NullLogger<SessionScoredConsumer>.Instance);
        var channel = new Mock<IChannel>(MockBehavior.Loose);

        await consumer.DeclareTopologyAsync(channel.Object, default);

        channel.Verify(c => c.QueueBindAsync(
            "campaign.ranking", "interview.events", "session.abandoned", null, false, It.IsAny<CancellationToken>()),
            Times.Once);
    }

    private static string RepoRoot([CallerFilePath] string here = "")
        => Path.GetFullPath(Path.Combine(Path.GetDirectoryName(here)!, "..", "..", ".."));
}
