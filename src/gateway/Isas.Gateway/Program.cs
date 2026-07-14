using Isas.Gateway.Services;
using Isas.Shared.Extensions;
using Scalar.AspNetCore;

var builder = WebApplication.CreateBuilder(args);

builder.Services.AddGatewayCors(builder.Configuration);
builder.Services.AddReverseProxy()
    .LoadFromConfig(builder.Configuration.GetSection("ReverseProxy"));
builder.Services.AddHttpClient<OpenApiAggregatorService>();
builder.Services.AddHostedService<OpenApiAggregatorService>();

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

app.UseGatewayCors();

// GEN-1: /internal/* không qua gateway — payment-route pass-through root nên chặn tường minh.
app.Map("/api/v1/payment/internal/{**rest}", () => Results.NotFound()).ExcludeFromDescription();

app.MapReverseProxy();

app.Run();