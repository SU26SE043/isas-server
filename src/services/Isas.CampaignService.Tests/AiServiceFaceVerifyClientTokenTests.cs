using System.Net;
using System.Text;
using Isas.CampaignService.Services;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Logging.Abstractions;

namespace Isas.CampaignService.Tests;

/// <summary>
/// GEN-7 — AiServiceFaceVerifyClient phải đính X-Internal-Token khi gọi AIService /face-verify
/// (endpoint nay gate fail-closed như /decide-next). Bắt request outbound bằng stub handler.
/// </summary>
public class AiServiceFaceVerifyClientTokenTests
{
    private sealed class CapturingHandler : HttpMessageHandler
    {
        public HttpRequestMessage? Last { get; private set; }

        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken ct)
        {
            Last = request;
            return Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK)
            {
                Content = new StringContent(
                    "{\"faceCount\":1,\"match\":true,\"score\":0.9,\"signals\":[]}",
                    Encoding.UTF8, "application/json")
            });
        }
    }

    private static IConfiguration Config(string? token) =>
        new ConfigurationBuilder()
            .AddInMemoryCollection(new Dictionary<string, string?> { ["Internal:Token"] = token })
            .Build();

    [Fact]
    public async Task VerifyAsync_dinh_X_Internal_Token_tu_config()
    {
        var handler = new CapturingHandler();
        var http = new HttpClient(handler) { BaseAddress = new Uri("http://ai.local") };
        var client = new AiServiceFaceVerifyClient(
            http, Config("s3cr3t-internal"), NullLogger<AiServiceFaceVerifyClient>.Instance);

        await client.VerifyAsync("ref.jpg", "live.jpg");

        Assert.NotNull(handler.Last);
        Assert.True(handler.Last!.Headers.TryGetValues("X-Internal-Token", out var vals));
        Assert.Equal("s3cr3t-internal", Assert.Single(vals!));
    }
}
