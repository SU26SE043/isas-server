using Isas.InterviewService.Models;
using Isas.InterviewService.Services;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Logging.Abstractions;
using Moq;

namespace Isas.InterviewService.Tests;

public sealed class EntitlementClientTests
{
    [Fact]
    public async Task PaymentTimeout_FallsBackToFreeWithoutPremiumFeatures()
    {
        using var client = new HttpClient(new ThrowingHandler()) { BaseAddress = new Uri("http://payment.test") };
        var sut = new EntitlementClient(client, Mock.Of<IConfiguration>(), NullLogger<EntitlementClient>.Instance);

        var entitlement = await sut.ResolveUserAsync(Guid.NewGuid());

        Assert.Equal(EntitlementSnapshot.Free, entitlement);
        Assert.False(entitlement.AdaptiveEnabled);
        Assert.False(entitlement.GroundingEnabled);
        Assert.False(entitlement.CvAnalysisIncluded);
        Assert.False(entitlement.RepoAnalysisIncluded);
        Assert.False(entitlement.RoadmapEnabled);
    }

    private sealed class ThrowingHandler : HttpMessageHandler
    {
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken) =>
            throw new HttpRequestException("Payment unavailable");
    }
}
