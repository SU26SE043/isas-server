using Isas.Shared.Pagination;
using Microsoft.AspNetCore.Builder;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;

namespace Isas.Shared.Extensions;

public static class CorsExtensions
{
    public static IServiceCollection AddServiceCors(this IServiceCollection services, IConfiguration configuration)
    {
        var gatewayUrl = configuration["Gateway:Url"];

        services.AddCors(options =>
        {
            options.AddPolicy("GatewayOnly", policy =>
            {
                if (string.IsNullOrEmpty(gatewayUrl))
                    policy.AllowAnyOrigin().AllowAnyMethod().AllowAnyHeader()
                        .WithExposedHeaders(KeysetPaging.NextCursorHeader, "Retry-After");
                else
                    policy.WithOrigins(gatewayUrl).AllowAnyMethod().AllowAnyHeader()
                        .WithExposedHeaders(KeysetPaging.NextCursorHeader, "Retry-After");
            });
        });

        return services;
    }

    public static IServiceCollection AddGatewayCors(this IServiceCollection services, IConfiguration configuration)
    {
        var allowedOrigins = configuration.GetSection("Cors:AllowedOrigins").Get<string[]>() ?? [];

        services.AddCors(options =>
        {
            options.AddPolicy("FrontendOnly", policy =>
            {
                policy.WithOrigins(allowedOrigins)
                    .AllowAnyMethod()
                    .AllowAnyHeader()
                    .WithExposedHeaders(KeysetPaging.NextCursorHeader, "Retry-After");
            });
        });

        return services;
    }

    public static WebApplication UseServiceCors(this WebApplication app)
    {
        app.UseCors("GatewayOnly");
        return app;
    }

    public static WebApplication UseGatewayCors(this WebApplication app)
    {
        app.UseCors("FrontendOnly");
        return app;
    }
}
