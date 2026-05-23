using System.Text.Json;
using System.Text.Json.Nodes;

namespace Isas.Gateway.Services;

public class ApiServiceConfig
{
    public string Name { get; set; } = "";
    public string OpenApiUrl { get; set; } = "";
    public string Prefix { get; set; } = "";
}

public class OpenApiAggregatorService(HttpClient http, IConfiguration config) : BackgroundService
{
    public static string MergedDoc { get; private set; } = "{}";

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        while (!stoppingToken.IsCancellationRequested)
        {
            await RefreshOnce(http, config);
            await Task.Delay(TimeSpan.FromSeconds(30), stoppingToken);
        }
    }

    public static async Task RefreshOnce(HttpClient http, IConfiguration config)
    {
        var services = config.GetSection("ApiServices").Get<List<ApiServiceConfig>>() ?? [];

        var mergedPaths = new JsonObject();
        var mergedSchemas = new JsonObject();
        JsonObject? baseDoc = null;

        foreach (var service in services)
        {
            try
            {
                var json = await http.GetStringAsync(service.OpenApiUrl);
                var doc = JsonNode.Parse(json)?.AsObject();
                if (doc is null) continue;

                baseDoc ??= JsonNode.Parse(json)!.AsObject();

                var paths = doc["paths"]?.AsObject();
                if (paths is not null)
                {
                    foreach (var path in paths)
                    {
                        var pathJson = path.Value?.ToJsonString() ?? "{}";
                        pathJson = pathJson.Replace(
                            "#/components/schemas/",
                            $"#/components/schemas/{service.Name}_"
                        );
                        mergedPaths[service.Prefix + path.Key] = JsonNode.Parse(pathJson);
                    }
                }

                var schemas = doc["components"]?["schemas"]?.AsObject();
                if (schemas is not null)
                {
                    foreach (var schema in schemas)
                    {
                        var schemaJson = schema.Value?.ToJsonString() ?? "{}";
                        schemaJson = schemaJson.Replace(
                            "#/components/schemas/",
                            $"#/components/schemas/{service.Name}_"
                        );
                        mergedSchemas[$"{service.Name}_{schema.Key}"] = JsonNode.Parse(schemaJson);
                    }
                }
            }
            catch
            {
                // Service chưa chạy thì bỏ qua
            }
        }

        if (baseDoc is null) return;

        var gatewayUrl = config["Gateway:Url"]!;
        var title = config["Gateway:Title"]!;
        var version = config["Gateway:Version"]!;
        var description = config["Gateway:Description"]!;

        baseDoc["info"] = JsonNode.Parse($"{{\"title\":\"{title}\",\"version\":\"{version}\"}}");
        baseDoc["paths"] = mergedPaths;
        baseDoc["servers"] = JsonNode.Parse($"[{{\"url\":\"{gatewayUrl}\",\"description\":\"{description}\"}}]");

        if (baseDoc["components"] is null)
            baseDoc["components"] = new JsonObject();

        baseDoc["components"]!["schemas"] = mergedSchemas;
        baseDoc["components"]!["securitySchemes"] = JsonNode.Parse("""
        {
            "Bearer": {
                "type": "http",
                "scheme": "bearer",
                "bearerFormat": "JWT"
            }
        }
        """);

        baseDoc["security"] = JsonNode.Parse("""[{"Bearer":[]}]""");

        MergedDoc = baseDoc.ToJsonString(new JsonSerializerOptions { WriteIndented = true });
    }
}