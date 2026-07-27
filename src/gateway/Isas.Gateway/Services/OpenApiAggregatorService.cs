using System.Text.Json;
using System.Text.Json.Nodes;

namespace Isas.Gateway.Services;

public class ApiServiceConfig
{
    public string OpenApiUrl { get; set; } = "";
    public string Prefix { get; set; } = "";
    // Base path gốc của service cần cắt bỏ trước khi gắn Prefix gateway.
    // Vd: Interview trả path "/api/practice/sessions", StripPrefix="/api"
    // -> cắt còn "/practice/sessions" -> + Prefix "/api/v1/interview" = "/api/v1/interview/practice/sessions"
    public string StripPrefix { get; set; } = "";
}

public class OpenApiAggregatorService(
    HttpClient http,
    IConfiguration config,
    IHostEnvironment env,
    ILogger<OpenApiAggregatorService> logger) : BackgroundService
{
    public static string MergedDoc { get; private set; } = "{}";

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        while (!stoppingToken.IsCancellationRequested)
        {
            // Development: doc hiện TOÀN BỘ api (kể cả /internal/*) để Scalar test được hết —
            // internal cũng được route qua gateway ở dev (appsettings.Development.json).
            // Production: loại internal khỏi doc VÀ chặn route (Program.cs) — GEN-1 khoá lại.
            await RefreshOnce(http, config, logger, includeInternal: env.IsDevelopment());
            await Task.Delay(TimeSpan.FromSeconds(30), stoppingToken);
        }
    }

    // /internal/* = callback nội bộ. Production: loại khỏi doc (không route qua gateway -> bấm 404).
    // Development: giữ trong doc để show toàn bộ api (route dev-only cho gọi được).
    private static bool IsInternalPath(string path) =>
        path.StartsWith("/internal/", StringComparison.OrdinalIgnoreCase);

    public static async Task RefreshOnce(
        HttpClient http,
        IConfiguration config,
        ILogger? logger = null,
        bool includeInternal = false)
    {
        // Keyed-by-name: env override là "ApiServices__<name>__OpenApiUrl", nên URL của một service
        // không thể bị ghép với Prefix của service khác. Mảng-theo-index trước đây làm đúng chuyện đó:
        // compose khai 5 URL còn appsettings khai 4 entry -> mọi service lệch một ô prefix.
        var services = config.GetSection("ApiServices").Get<Dictionary<string, ApiServiceConfig>>() ?? [];

        var mergedPaths = new JsonObject();
        var mergedSchemas = new JsonObject();
        JsonObject? baseDoc = null;

        foreach (var (name, service) in services)
        {
            // Prefix rỗng = mọi path của service đó đổ ra root doc, đúng triệu chứng của lỗi cấu hình
            // cũ (env "ApiServices__4__OpenApiUrl" không có entry tương ứng trong appsettings).
            // Thà mất một service trong doc còn hơn sinh ra doc sai mà không ai biết.
            if (string.IsNullOrWhiteSpace(service.Prefix))
            {
                logger?.LogError(
                    "ApiServices:{Service} không có Prefix nên bị bỏ qua. Env phải theo tên "
                    + "(ApiServices__<tên>__OpenApiUrl), không phải theo index.", name);
                continue;
            }

            try
            {
                var json = await http.GetStringAsync(service.OpenApiUrl);
                var doc = JsonNode.Parse(json)?.AsObject();
                if (doc is null) continue;

                baseDoc ??= JsonNode.Parse(json)!.AsObject();

                var paths = doc["paths"]?.AsObject();
                var merged = 0;
                if (paths is not null)
                {
                    foreach (var path in paths)
                    {
                        if (!includeInternal && IsInternalPath(path.Key)) continue;

                        var pathJson = path.Value?.ToJsonString() ?? "{}";
                        pathJson = pathJson.Replace(
                            "#/components/schemas/",
                            $"#/components/schemas/{name}_"
                        );

                        // Cắt base path gốc rồi mới gắn Prefix gateway, tránh double prefix.
                        var key = path.Key;
                        if (!string.IsNullOrEmpty(service.StripPrefix)
                            && key.StartsWith(service.StripPrefix))
                        {
                            key = key[service.StripPrefix.Length..];
                        }

                        mergedPaths[service.Prefix + key] = JsonNode.Parse(pathJson);
                        merged++;
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
                            $"#/components/schemas/{name}_"
                        );
                        mergedSchemas[$"{name}_{schema.Key}"] = JsonNode.Parse(schemaJson);
                    }
                }

                logger?.LogInformation(
                    "OpenAPI merge: {Service} -> {Prefix} ({Count} path) từ {Url}",
                    name, service.Prefix, merged, service.OpenApiUrl);
            }
            catch (Exception ex)
            {
                // Nuốt im lặng ở đây từng khiến cấu hình lệch chạy nhiều ngày mà không ai thấy:
                // doc vẫn sinh ra, chỉ thiếu/sai service. Phải log ra.
                logger?.LogWarning(ex,
                    "Không nạp được OpenAPI của service {Service} từ {Url}",
                    name, service.OpenApiUrl);
            }
        }

        if (baseDoc is null)
        {
            logger?.LogWarning("Không service nào trả OpenAPI — giữ nguyên doc cũ.");
            return;
        }

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
