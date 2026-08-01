using Isas.CampaignService.Models;
using Isas.CampaignService.Services;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Caching.Memory;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using Moq;

namespace Isas.CampaignService.Tests;

public sealed class EntitlementClientTests
{
    [Fact]
    public async Task TieringDisabled_ReturnsLegacyWithoutCallingPayment()
    {
        var handler = new CountingHandler();
        using var client = new HttpClient(handler) { BaseAddress = new Uri("http://payment.test") };
        using var cache = new MemoryCache(new MemoryCacheOptions());
        var config = Mock.Of<IConfiguration>();
        var sut = new EntitlementClient(client, config, cache, NullLogger<EntitlementClient>.Instance,
            Options.Create(new TieringSettings { Enabled = false }));

        var entitlement = await sut.ResolveOrgAsync(Guid.NewGuid());

        Assert.Equal(CampaignEntitlement.Legacy, entitlement);
        Assert.Equal(0, handler.Calls);
    }

    [Fact]
    public async Task TieringEnabled_PaymentTimeout_FallsBackToStarter()
    {
        using var client = new HttpClient(new ThrowingHandler()) { BaseAddress = new Uri("http://payment.test") };
        using var cache = new MemoryCache(new MemoryCacheOptions());
        var sut = new EntitlementClient(client, Mock.Of<IConfiguration>(), cache, NullLogger<EntitlementClient>.Instance,
            Options.Create(new TieringSettings { Enabled = true }));

        var entitlement = await sut.ResolveOrgAsync(Guid.NewGuid());

        Assert.Equal(CampaignEntitlement.Starter, entitlement);
    }

    private sealed class CountingHandler : HttpMessageHandler
    {
        public int Calls { get; private set; }
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            Calls++;
            return Task.FromResult(new HttpResponseMessage(System.Net.HttpStatusCode.OK));
        }
    }

    private sealed class ThrowingHandler : HttpMessageHandler
    {
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken) =>
            throw new HttpRequestException("Payment unavailable");
    }
}
