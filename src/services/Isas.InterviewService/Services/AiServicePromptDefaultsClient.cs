using System.Collections.Concurrent;
using System.Net.Http.Json;
using System.Text.Json;

namespace Isas.InterviewService.Services;

/// <summary>Bản mặc định của các mảnh prompt (F21) — nguồn duy nhất là mã AIService.</summary>
public interface IPromptDefaultsProvider
{
    /// <summary>
    /// Khoá → văn bản mặc định. <c>null</c> = không lấy được (AIService chưa cấu hình/không tới được/
    /// trả sai) — caller PHẢI truyền tiếp <c>null</c>, không thay bằng bản đồ rỗng: rỗng nghĩa là
    /// "mọi khe mặc định trống", một lời nói dối khác.
    /// </summary>
    Task<IReadOnlyDictionary<string, string>?> GetDefaultsAsync(CancellationToken ct = default);
}

/// <summary>
/// Kéo <c>GET /api/v1/prompt-defaults</c> của AIService (X-Internal-Token, GEN-7), cache theo TTL
/// (mặc định 5 phút — literal trong code chỉ đổi khi deploy image mới). FAIL-OPEN: mọi lỗi ⇒ giữ
/// cache cũ nếu có, không thì <c>null</c>; màn admin vẫn dùng được, chỉ thiếu cột "mặc định".
/// Cùng triết lý <c>prompt_registry.py</c> phía Python: chức năng phụ không kéo đổ đường chính.
/// </summary>
public class AiServicePromptDefaultsClient : IPromptDefaultsProvider
{
    private static readonly JsonSerializerOptions Json = new() { PropertyNameCaseInsensitive = true };
    private static readonly ConcurrentDictionary<string, (IReadOnlyDictionary<string, string> Map, DateTimeOffset At)> Cache = new();
    private const string CacheKey = "defaults";

    private readonly HttpClient _http;
    private readonly string? _internalToken;
    private readonly TimeSpan _ttl;
    private readonly ILogger<AiServicePromptDefaultsClient> _logger;

    public AiServicePromptDefaultsClient(HttpClient http, IConfiguration config, ILogger<AiServicePromptDefaultsClient> logger)
    {
        _http = http;
        _internalToken = config["Internal:Token"];
        _ttl = TimeSpan.FromSeconds(config.GetValue<int?>("PromptDefaults:CacheTtlSeconds") ?? 300);
        _logger = logger;
    }

    private sealed record Payload(Dictionary<string, string>? Defaults);

    public async Task<IReadOnlyDictionary<string, string>?> GetDefaultsAsync(CancellationToken ct = default)
    {
        if (Cache.TryGetValue(CacheKey, out var hit) && DateTimeOffset.UtcNow - hit.At < _ttl)
            return hit.Map;

        if (_http.BaseAddress is null)
        {
            _logger.LogWarning("PromptDefaults: AiService:BaseUrl chưa cấu hình — màn admin sẽ không hiện bản mặc định.");
            return hit.Map;
        }

        try
        {
            using var req = new HttpRequestMessage(HttpMethod.Get, "api/v1/prompt-defaults");
            if (!string.IsNullOrWhiteSpace(_internalToken)) req.Headers.Add("X-Internal-Token", _internalToken);
            using var res = await _http.SendAsync(req, ct);
            if (!res.IsSuccessStatusCode)
            {
                _logger.LogWarning("PromptDefaults: AIService trả {Status} — giữ cache cũ (nếu có).", (int)res.StatusCode);
                return hit.Map;
            }
            var payload = await res.Content.ReadFromJsonAsync<Payload>(Json, ct);
            if (payload?.Defaults is null)
            {
                _logger.LogWarning("PromptDefaults: phản hồi thiếu `defaults` — hợp đồng lệch, giữ cache cũ.");
                return hit.Map;
            }
            IReadOnlyDictionary<string, string> map = payload.Defaults;
            Cache[CacheKey] = (map, DateTimeOffset.UtcNow);
            return map;
        }
        catch (Exception ex) when (ex is HttpRequestException or TaskCanceledException or JsonException)
        {
            _logger.LogWarning(ex, "PromptDefaults: không lấy được bản mặc định từ AIService — fail-open.");
            return hit.Map;
        }
    }

    /// <summary>Cho test: xoá cache tĩnh giữa các ca.</summary>
    public static void ResetCache() => Cache.Clear();
}
