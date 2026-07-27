using System.Net;
using System.Net.Http.Headers;
using System.Text;
using System.Text.Json;
using Isas.InterviewService.Services.Interfaces;

namespace Isas.InterviewService.Services;

public sealed class GitHubRateLimitException(string message, string? retryAfter = null) : Exception(message)
{
    public string? RetryAfter { get; } = retryAfter;
}

public class GitHubRepoFetcher(HttpClient client, IConfiguration config) : IGitHubRepoFetcher
{
    private const int DigestMax = 30_000, ReadmeMax = 8_000, FileMax = 4_000, TreeMax = 300, FileCountMax = 5;
    private readonly string? _token = config["GitHub:Token"];
    private static readonly JsonSerializerOptions Json = new() { PropertyNameCaseInsensitive = true };
    private sealed record Repo(string? Default_Branch, int Stargazers_Count, string? Language, bool Private, long Size);
    private sealed record TreeResponse(string? Sha, bool Truncated, List<TreeItem>? Tree);
    private sealed record TreeItem(string? Path, string? Type, long? Size);

    public async Task<GitHubRepoDigest> FetchAsync(string owner, string repo, CancellationToken ct = default)
    {
        var info = await GetJson<Repo>($"repos/{owner}/{repo}", ct);
        if (info.Private || info.Size == 0 || string.IsNullOrWhiteSpace(info.Default_Branch))
            throw new InvalidOperationException("Repository rỗng hoặc không thể phân tích.");
        var languages = await GetJson<Dictionary<string,long>>($"repos/{owner}/{repo}/languages", ct);
        var tree = await GetJson<TreeResponse>($"repos/{owner}/{repo}/git/trees/{Uri.EscapeDataString(info.Default_Branch)}?recursive=1", ct);
        var paths = (tree.Tree ?? []).Take(TreeMax).Where(x => x.Type == "blob" && IsUseful(x.Path)).ToList();
        var selected = paths.OrderByDescending(x => ManifestPriority(x.Path)).ThenByDescending(x => x.Size ?? 0).Take(FileCountMax).ToList();
        var digest = new StringBuilder($"Repository: {owner}/{repo}\nDefault branch: {info.Default_Branch}\nStars: {info.Stargazers_Count}\nPrimary language: {info.Language ?? "unknown"}\nLanguages: {string.Join(", ", languages.Select(x => $"{x.Key}={x.Value}"))}\n");
        if (tree.Truncated) digest.AppendLine("Tree was truncated by GitHub; only the returned paths were inspected.");
        var readme = await TryGetRaw($"repos/{owner}/{repo}/readme", ct);
        if (!string.IsNullOrWhiteSpace(readme)) Append(digest, "README", readme, ReadmeMax);
        foreach (var file in selected)
        {
            var content = await TryGetRaw($"repos/{owner}/{repo}/contents/{file.Path}", ct);
            if (!string.IsNullOrWhiteSpace(content)) Append(digest, file.Path!, content, FileMax);
            if (digest.Length >= DigestMax) break;
        }
        var sha = tree.Sha;
        return new(info.Default_Branch, sha, info.Stargazers_Count, info.Language, languages, digest.ToString()[..Math.Min(digest.Length, DigestMax)]);
    }

    private static bool IsUseful(string? path) => path is not null && !path.Contains("node_modules/", StringComparison.OrdinalIgnoreCase) && !path.Contains("/vendor/", StringComparison.OrdinalIgnoreCase) && !path.Contains("/dist/", StringComparison.OrdinalIgnoreCase) && !path.Contains(".min.", StringComparison.OrdinalIgnoreCase) && !path.EndsWith(".lock", StringComparison.OrdinalIgnoreCase) && !path.EndsWith("package-lock.json", StringComparison.OrdinalIgnoreCase);
    private static int ManifestPriority(string? path) => path?.ToLowerInvariant() switch { "package.json" => 100, "requirements.txt" => 100, "pom.xml" => 100, "go.mod" => 100, _ when path?.EndsWith(".csproj", StringComparison.OrdinalIgnoreCase) == true => 100, _ => 0 };
    private static void Append(StringBuilder to, string title, string content, int limit) => to.Append("\n--- ").Append(title).Append(" ---\n").Append(content[..Math.Min(content.Length, limit)]).AppendLine("\n--- end ---");
    private async Task<T> GetJson<T>(string path, CancellationToken ct)
    {
        using var response = await Send(path, "application/vnd.github+json", ct);
        if (response.StatusCode == HttpStatusCode.NotFound) throw new KeyNotFoundException("Repository không tồn tại hoặc private.");
        await EnsureSuccess(response, ct);
        return await response.Content.ReadFromJsonAsync<T>(Json, ct) ?? throw new InvalidOperationException("GitHub trả dữ liệu rỗng.");
    }
    private async Task<string?> TryGetRaw(string path, CancellationToken ct)
    {
        using var response = await Send(path, "application/vnd.github.raw+json", ct);
        if (response.StatusCode == HttpStatusCode.NotFound) return null;
        await EnsureSuccess(response, ct);
        return await response.Content.ReadAsStringAsync(ct);
    }
    private async Task<HttpResponseMessage> Send(string path, string accept, CancellationToken ct)
    {
        using var req = new HttpRequestMessage(HttpMethod.Get, path);
        req.Headers.Accept.ParseAdd(accept);
        if (!string.IsNullOrWhiteSpace(_token)) req.Headers.Authorization = new AuthenticationHeaderValue("Bearer", _token);
        try { return await client.SendAsync(req, ct); } catch (Exception ex) when (ex is HttpRequestException or TaskCanceledException) { throw new InvalidOperationException("Không gọi được GitHub API.", ex); }
    }
    private static async Task EnsureSuccess(HttpResponseMessage response, CancellationToken ct)
    {
        if (response.StatusCode == HttpStatusCode.TooManyRequests || (response.StatusCode == HttpStatusCode.Forbidden && response.Headers.TryGetValues("X-RateLimit-Remaining", out var remain) && remain.SingleOrDefault() == "0"))
            throw new GitHubRateLimitException("GitHub API đã chạm rate limit.", response.Headers.RetryAfter?.Delta?.TotalSeconds.ToString("0"));
        if (!response.IsSuccessStatusCode) throw new InvalidOperationException($"GitHub API trả {(int)response.StatusCode}: {await response.Content.ReadAsStringAsync(ct)}");
    }
}
