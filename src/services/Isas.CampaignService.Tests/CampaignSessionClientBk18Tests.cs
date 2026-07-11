using System.Net;
using System.Text;
using System.Text.Json;
using Isas.CampaignService.Services;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Logging.Abstractions;

namespace Isas.CampaignService.Tests;

/// <summary>
/// BK18 — CampaignSessionClient PHẢI đưa expiresAt vào body JSON gửi Interview
/// /internal/sessions/campaign, để Interview (I2) set session.Deadline. Stub HttpMessageHandler bắt payload.
/// </summary>
public class CampaignSessionClientBk18Tests
{
    private sealed class CapturingHandler : HttpMessageHandler
    {
        public string? CapturedBody { get; private set; }
        private readonly Guid _sessionId;
        public CapturingHandler(Guid sessionId) => _sessionId = sessionId;

        protected override async Task<HttpResponseMessage> SendAsync(
            HttpRequestMessage request, CancellationToken cancellationToken)
        {
            CapturedBody = request.Content is null
                ? null
                : await request.Content.ReadAsStringAsync(cancellationToken);
            var json = $"{{\"id\":\"{_sessionId}\",\"questions\":[]}}";
            return new HttpResponseMessage(HttpStatusCode.OK)
            {
                Content = new StringContent(json, Encoding.UTF8, "application/json")
            };
        }
    }

    private static CampaignSessionClient NewClient(CapturingHandler handler)
    {
        var http = new HttpClient(handler) { BaseAddress = new Uri("http://interview.test") };
        var config = new ConfigurationBuilder()
            .AddInMemoryCollection(new Dictionary<string, string?> { ["Internal:Token"] = "tkn" })
            .Build();
        return new CampaignSessionClient(http, config, NullLogger<CampaignSessionClient>.Instance);
    }

    private static readonly IReadOnlyList<string> Questions = new List<string> { "Q1" };
    private static readonly IReadOnlyList<SessionCriterionInput> Criteria =
        new List<SessionCriterionInput> { new("Communication", null, 1.0m, 5) };

    [Fact]
    public async Task Payload_ChuaExpiresAt_KhiCampaignCoHan()
    {
        var handler = new CapturingHandler(Guid.NewGuid());
        var client = NewClient(handler);
        var deadline = new DateTime(2026, 8, 1, 10, 0, 0, DateTimeKind.Utc);

        await client.CreateOrGetSessionAsync(
            Guid.NewGuid(), Guid.NewGuid(), "BE", Questions, Criteria, deadline, default);

        Assert.NotNull(handler.CapturedBody);
        using var doc = JsonDocument.Parse(handler.CapturedBody!);
        Assert.True(doc.RootElement.TryGetProperty("expiresAt", out var exp));
        Assert.Equal(deadline, exp.GetDateTime());
    }

    [Fact]
    public async Task Payload_ExpiresAtNull_KhiCampaignKhongHan()
    {
        var handler = new CapturingHandler(Guid.NewGuid());
        var client = NewClient(handler);

        await client.CreateOrGetSessionAsync(
            Guid.NewGuid(), Guid.NewGuid(), "BE", Questions, Criteria, null, default);

        Assert.NotNull(handler.CapturedBody);
        using var doc = JsonDocument.Parse(handler.CapturedBody!);
        Assert.True(doc.RootElement.TryGetProperty("expiresAt", out var exp));
        Assert.Equal(JsonValueKind.Null, exp.ValueKind);
    }
}
