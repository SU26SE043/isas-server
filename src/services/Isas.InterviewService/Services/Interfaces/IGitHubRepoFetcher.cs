namespace Isas.InterviewService.Services.Interfaces;

public interface IGitHubRepoFetcher
{
    Task<GitHubRepoDigest> FetchAsync(string owner, string repo, CancellationToken ct = default);
}

public record GitHubRepoDigest(string DefaultBranch, string? CommitSha, int Stars,
    string? PrimaryLanguage, Dictionary<string, long> Languages, string Digest);
