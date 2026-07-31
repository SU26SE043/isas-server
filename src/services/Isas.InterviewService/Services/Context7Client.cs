using System.Text.Json;
using Isas.InterviewService.Services.Interfaces;

namespace Isas.InterviewService.Services;

// RAG grounding — typed HttpClient gọi Context7 (context7.com/api/v2). Bearer {Context7:ApiKey}.
// Xử 429 (Retry-After). Parse PHÒNG THỦ bằng JsonDocument (thử nhiều tên field) vì shape v2 của Context7
// có thể đổi — ⚠ CẦN L3 verify với API + key thật (không mock được shape thật ở unit test).
public class Context7Client(HttpClient client, IConfiguration config, ILogger<Context7Client> logger)
    : IContext7Client
{
    private readonly string? _apiKey = config["Context7:ApiKey"];

    public async Task<IReadOnlyList<Context7Library>> SearchAsync(
        string libraryName, string? query, CancellationToken ct = default)
    {
        var q = Uri.EscapeDataString(query ?? libraryName);
        using var doc = await GetJsonAsync($"/libs/search?libraryName={Uri.EscapeDataString(libraryName)}&query={q}", ct);

        var results = new List<Context7Library>();
        // Kết quả có thể nằm ở root array HOẶC dưới "results"/"libraries".
        var arr = FindArray(doc.RootElement, "results", "libraries");
        foreach (var item in arr)
        {
            var id = Str(item, "id", "libraryId", "settings.project");
            if (string.IsNullOrEmpty(id)) continue;
            results.Add(new Context7Library(
                id,
                Str(item, "title", "name") ?? id,
                Str(item, "reputation", "trustScore", "stars"),
                Int(item, "snippets", "totalSnippets", "totalTokens")));
        }
        return results;
    }

    public async Task<IReadOnlyList<Context7Snippet>> GetContextAsync(
        string libraryId, string topic, CancellationToken ct = default)
    {
        // type=json để nhận snippet có cấu trúc (title/description/code/source) thay vì markdown thô.
        using var doc = await GetJsonAsync(
            $"/context?library={Uri.EscapeDataString(libraryId)}&topic={Uri.EscapeDataString(topic)}&type=json", ct);

        var snippets = new List<Context7Snippet>();
        var arr = FindArray(doc.RootElement, "snippets", "results");
        foreach (var s in arr)
        {
            var title = Str(s, "codeTitle", "pageTitle", "title") ?? topic;
            var desc = Str(s, "codeDescription", "description", "content") ?? string.Empty;
            var code = ExtractCode(s);
            var body = string.Join("\n\n", new[] { desc, code }.Where(x => !string.IsNullOrWhiteSpace(x))).Trim();
            if (string.IsNullOrWhiteSpace(body)) continue;
            snippets.Add(new Context7Snippet(
                title.Trim(), body, Str(s, "source", "pageUrl", "sourceUrl", "url")));
        }
        return snippets;
    }

    // ── HTTP ────────────────────────────────────────────────────────────────
    private async Task<JsonDocument> GetJsonAsync(string path, CancellationToken ct)
    {
        using var request = new HttpRequestMessage(HttpMethod.Get, path);
        if (!string.IsNullOrEmpty(_apiKey))
            request.Headers.TryAddWithoutValidation("Authorization", $"Bearer {_apiKey}");

        HttpResponseMessage response;
        try
        {
            response = await client.SendAsync(request, ct);
        }
        catch (Exception ex) when (ex is HttpRequestException or TaskCanceledException)
        {
            logger.LogError(ex, "Không gọi được Context7 {Path}", path);
            throw new Context7Exception("Không gọi được Context7", ex);
        }

        if ((int)response.StatusCode == 429)
        {
            var retryAfter = response.Headers.TryGetValues("Retry-After", out var vals) ? vals.FirstOrDefault() : null;
            throw new Context7RateLimitException("Context7 giới hạn tốc độ (429)", retryAfter);
        }
        if (!response.IsSuccessStatusCode)
        {
            logger.LogError("Context7 {Path} trả {Status}", path, response.StatusCode);
            throw new Context7Exception($"Context7 trả {(int)response.StatusCode}");
        }

        var raw = await response.Content.ReadAsStringAsync(ct);
        try
        {
            return JsonDocument.Parse(raw);
        }
        catch (JsonException ex)
        {
            logger.LogError(ex, "Context7 {Path} trả JSON không hợp lệ", path);
            throw new Context7Exception("Context7 trả JSON không hợp lệ", ex);
        }
    }

    // ── Parse phòng thủ ───────────────────────────────────────────────────────
    private static IEnumerable<JsonElement> FindArray(JsonElement root, params string[] keys)
    {
        if (root.ValueKind == JsonValueKind.Array) return root.EnumerateArray();
        if (root.ValueKind == JsonValueKind.Object)
            foreach (var k in keys)
                if (root.TryGetProperty(k, out var v) && v.ValueKind == JsonValueKind.Array)
                    return v.EnumerateArray();
        return Array.Empty<JsonElement>();
    }

    private static string? Str(JsonElement el, params string[] keys)
    {
        if (el.ValueKind != JsonValueKind.Object) return null;
        foreach (var k in keys)
        {
            // Hỗ trợ path 1 cấp "a.b".
            var parts = k.Split('.');
            var cur = el;
            var ok = true;
            foreach (var p in parts)
            {
                if (cur.ValueKind == JsonValueKind.Object && cur.TryGetProperty(p, out var nxt)) cur = nxt;
                else { ok = false; break; }
            }
            if (!ok) continue;
            if (cur.ValueKind == JsonValueKind.String) return cur.GetString();
            if (cur.ValueKind is JsonValueKind.Number) return cur.ToString();
        }
        return null;
    }

    private static int Int(JsonElement el, params string[] keys)
    {
        if (el.ValueKind != JsonValueKind.Object) return 0;
        foreach (var k in keys)
            if (el.TryGetProperty(k, out var v) && v.ValueKind == JsonValueKind.Number && v.TryGetInt32(out var n))
                return n;
        return 0;
    }

    // Ghép code từ "codeList":[{code}] hoặc field "code" đơn.
    private static string ExtractCode(JsonElement s)
    {
        if (s.ValueKind != JsonValueKind.Object) return string.Empty;
        if (s.TryGetProperty("code", out var single) && single.ValueKind == JsonValueKind.String)
            return single.GetString() ?? string.Empty;
        if (s.TryGetProperty("codeList", out var list) && list.ValueKind == JsonValueKind.Array)
            return string.Join("\n\n", list.EnumerateArray()
                .Select(c => c.TryGetProperty("code", out var cc) ? cc.GetString() : null)
                .Where(x => !string.IsNullOrWhiteSpace(x)));
        return string.Empty;
    }
}
