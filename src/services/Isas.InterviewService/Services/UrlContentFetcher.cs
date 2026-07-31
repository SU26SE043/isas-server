using Isas.InterviewService.Services.Interfaces;

namespace Isas.InterviewService.Services;

// RAG grounding — tải HTML URL admin nhập (ingest nguồn Url). Cap kích thước để chống nuốt trang khổng lồ.
// Admin-only (Roles=Admin) hạn chế bề mặt SSRF; L3 nên cân nhắc allowlist host nếu mở rộng người dùng.
public class UrlContentFetcher(HttpClient client, ILogger<UrlContentFetcher> logger) : IUrlContentFetcher
{
    private const int MaxBytes = 5 * 1024 * 1024;   // 5MB — trang tài liệu bình thường xa dưới ngưỡng này.

    public async Task<string> FetchAsync(string url, CancellationToken ct = default)
    {
        if (!Uri.TryCreate(url, UriKind.Absolute, out var uri)
            || (uri.Scheme != Uri.UriSchemeHttp && uri.Scheme != Uri.UriSchemeHttps))
            throw new InvalidOperationException("URL không hợp lệ (chỉ http/https).");

        HttpResponseMessage response;
        try
        {
            response = await client.GetAsync(uri, HttpCompletionOption.ResponseHeadersRead, ct);
        }
        catch (Exception ex) when (ex is HttpRequestException or TaskCanceledException)
        {
            logger.LogWarning(ex, "Không tải được URL {Url}", url);
            throw new InvalidOperationException($"Không tải được URL: {url}");
        }

        if (!response.IsSuccessStatusCode)
            throw new InvalidOperationException($"URL trả {(int)response.StatusCode}: {url}");

        var bytes = await response.Content.ReadAsByteArrayAsync(ct);
        if (bytes.Length > MaxBytes)
            throw new InvalidOperationException($"Nội dung URL vượt {MaxBytes / (1024 * 1024)}MB.");

        return System.Text.Encoding.UTF8.GetString(bytes);
    }
}
