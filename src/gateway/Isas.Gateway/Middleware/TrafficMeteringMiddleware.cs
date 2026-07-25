using System.Diagnostics;
using Isas.Shared.Analytics;
using Isas.Gateway.Services;
using Microsoft.Extensions.Options;
using Yarp.ReverseProxy.Model;

namespace Isas.Gateway.Middleware;
public sealed class TrafficMeteringMiddleware(RequestDelegate next, HttpTrafficAggregator aggregator, IOptions<TrafficAnalyticsOptions> options)
{
    public async Task Invoke(HttpContext context)
    {
        if (!options.Value.Enabled || HttpMethods.IsOptions(context.Request.Method) || context.Request.Path.StartsWithSegments("/openapi") || context.Request.Path.StartsWithSegments("/scalar")) { await next(context); return; }
        var watch = Stopwatch.StartNew();
        try { await next(context); }
        finally
        {
            var route = context.Features.Get<IReverseProxyFeature>()?.Route.Config.RouteId ?? "unmatched";
            aggregator.Record(route, context.Response.StatusCode, watch.ElapsedMilliseconds);
        }
    }
}
