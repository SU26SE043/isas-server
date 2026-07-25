using System.Net.Http.Json;
using Isas.Shared.Analytics;
using Microsoft.Extensions.Options;

namespace Isas.Gateway.Services;
public sealed class TrafficFlushService(HttpTrafficAggregator aggregator, IHttpClientFactory clients, IConfiguration config, IOptions<TrafficAnalyticsOptions> options, ILogger<TrafficFlushService> logger) : BackgroundService
{
    protected override async Task ExecuteAsync(CancellationToken ct)
    {
        var o = options.Value;
        if (!o.Enabled || !Uri.TryCreate(o.SinkBaseUrl, UriKind.Absolute, out var sink) || string.IsNullOrWhiteSpace(config["Internal:Token"])) { logger.LogInformation("FR18 traffic flush disabled or incomplete configuration"); return; }
        var interval = TimeSpan.FromSeconds(o.FlushIntervalSeconds > 0 ? o.FlushIntervalSeconds : 300);
        while (!ct.IsCancellationRequested)
        {
            try
            {
                foreach (var stat in aggregator.Drain())
                {
                    using var req = new HttpRequestMessage(HttpMethod.Post, new Uri(sink, "/internal/http-traffic")) { Content = JsonContent.Create(stat) };
                    req.Headers.Add("X-Internal-Token", config["Internal:Token"]);
                    await clients.CreateClient("traffic-sink").SendAsync(req, ct);
                }
            }
            catch (Exception ex) { logger.LogWarning(ex, "FR18 traffic flush failed; current drained window may be lost (at-most-once)"); }
            await Task.Delay(interval, ct);
        }
    }
}
