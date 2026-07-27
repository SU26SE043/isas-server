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

// GEN-1 (Production): chặn MỌI "/api/v1/<svc>/internal/*" — callback nội bộ KHÔNG được qua gateway.
// Chạy sau CollapseSlashes (bắt cả "//internal") và TRƯỚC UseRouting nên độc lập với route nào.
// Development KHÔNG thêm middleware này ⇒ 4 route "<svc>-internal-route" (appsettings.json) forward
// internal qua gateway để Scalar test được TOÀN BỘ api. Production: middleware 404 kín, dù route
// internal vẫn nằm trong config (đây là hàng rào duy nhất, thay khối chặn payment-only cũ).
if (!app.Environment.IsDevelopment())
{
    app.Use(async (context, next) =>
    {
        if (IsGatewayInternalPath(context.Request.Path.Value))
        {
            context.Response.StatusCode = StatusCodes.Status404NotFound;
            return;
        }
        await next();
    });
}

app.UseRouting();

app.UseMiddleware<TrafficMeteringMiddleware>();

app.UseGatewayCors();

app.MapReverseProxy();

app.Run();

// Khớp "/api/v1/<svc>/internal" theo sau là "/" hoặc hết chuỗi. KHÔNG match "/api/v1/x/internalfoo".
static bool IsGatewayInternalPath(string? path)
{
    if (path is null) return false;
    const string prefix = "/api/v1/";
    if (!path.StartsWith(prefix, StringComparison.OrdinalIgnoreCase)) return false;
    var rest = path.AsSpan(prefix.Length);              // "<svc>/internal/..."
    var slash = rest.IndexOf('/');
    if (slash <= 0) return false;
    var afterSvc = rest[(slash + 1)..];                 // "internal/..." hoặc "internal"
    return afterSvc.Equals("internal", StringComparison.OrdinalIgnoreCase)
        || afterSvc.StartsWith("internal/", StringComparison.OrdinalIgnoreCase);
}

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