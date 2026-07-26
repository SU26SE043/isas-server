using System.Text;
using Isas.Gateway.Services;
using Isas.Gateway.Middleware;
using Isas.Shared.Analytics;
using Microsoft.Extensions.Options;
using Isas.Shared.Extensions;
using Scalar.AspNetCore;

var builder = WebApplication.CreateBuilder(args);

builder.Services.AddGatewayCors(builder.Configuration);
builder.Services.AddReverseProxy()
    .LoadFromConfig(builder.Configuration.GetSection("ReverseProxy"));
builder.Services.AddHttpClient<OpenApiAggregatorService>();
builder.Services.AddHostedService<OpenApiAggregatorService>();
builder.Services.Configure<TrafficAnalyticsOptions>(builder.Configuration.GetSection(TrafficAnalyticsOptions.SectionName));
var analytics = builder.Configuration.GetSection(TrafficAnalyticsOptions.SectionName).Get<TrafficAnalyticsOptions>() ?? new();
builder.Services.AddSingleton(new HttpTrafficAggregator(analytics.FlushIntervalSeconds > 0 ? analytics.FlushIntervalSeconds : 300, analytics.MaxPendingWindows));
builder.Services.AddHttpClient("traffic-sink");
builder.Services.AddHostedService<TrafficFlushService>();

var app = builder.Build();

if (app.Environment.IsDevelopment())
{
    // 1 endpoint duy nhất trả toàn bộ doc đã merge
    app.MapGet("/openapi/merged.json", () =>
        Results.Content(OpenApiAggregatorService.MergedDoc, "application/json")
    ).ExcludeFromDescription();

    // Scalar đọc từ merged doc
    app.MapScalarApiReference(options =>
    {
        options.Title = "ISAS Platform API";
        options.OpenApiRoutePattern = "/openapi/merged.json";
        options.WithDefaultHttpClient(ScalarTarget.JavaScript, ScalarClient.Fetch);
        
        options.AddPreferredSecuritySchemes("Bearer");
        options.AddHttpAuthentication("Bearer", auth =>
        {
            auth.Token = ""; // để trống, user tự nhập
        });
    });
}

// Chuẩn hoá dấu gạch chéo trùng (`//` → `/`) phải chạy TRƯỚC định tuyến. Không path REST hợp lệ nào
// chứa `//`, nhưng thiếu bước này thì `/api/v1/payment//internal/...` KHÔNG khớp khối chặn GEN-1 bên
// dưới (pattern chỉ khớp một `/`) rồi rơi xuống reverse-proxy strip-về-root ⇒ lọt tới controller
// Payment (đo được: `//internal/credits/reserve` → 401 "Invalid internal token" thay vì 404). Gộp ở
// đây bịt cả class (mọi service, mọi route strip-về-root), giống nginx `merge_slashes on` mặc định.
// Chỉ đụng Request.Path (không gồm query-string); `%2F` giữ nguyên mã hoá nên không bị gộp nhầm.
// ⚠ Phải đứng trước `UseRouting()` GỌI TƯỜNG MINH: nếu để framework tự chèn UseRouting ở đầu pipeline
// thì endpoint đã được match theo path CHƯA gộp, middleware này chạy quá muộn (đã đo: vẫn 401).
app.Use(async (context, next) =>
{
    var path = context.Request.Path.Value;
    if (path is not null && path.Contains("//", StringComparison.Ordinal))
        context.Request.Path = CollapseSlashes(path);
    await next();
});

app.UseRouting();

app.UseMiddleware<TrafficMeteringMiddleware>();

app.UseGatewayCors();

// GEN-1: /internal/* không qua gateway — payment-route pass-through root nên chặn tường minh.
app.Map("/api/v1/payment/internal/{**rest}", () => Results.NotFound()).ExcludeFromDescription();

app.MapReverseProxy();

app.Run();

// Gộp mọi chuỗi `/` liên tiếp thành một, chỉ cấp phát khi thật sự có `//` (đã lọc ở caller).
static string CollapseSlashes(string path)
{
    var sb = new StringBuilder(path.Length);
    var prevSlash = false;
    foreach (var c in path)
    {
        if (c == '/')
        {
            if (prevSlash) continue;
            prevSlash = true;
        }
        else
        {
            prevSlash = false;
        }
        sb.Append(c);
    }
    return sb.ToString();
}