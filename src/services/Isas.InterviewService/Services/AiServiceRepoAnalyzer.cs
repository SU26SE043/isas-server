using System.Net.Http.Json;
using System.Text.Json;
using Isas.InterviewService.DTOs;
using Isas.InterviewService.Entities;
using Isas.InterviewService.Services.Interfaces;

namespace Isas.InterviewService.Services;

public class AiServiceRepoAnalyzer(HttpClient client, IConfiguration config, ILogger<AiServiceRepoAnalyzer> logger) : IAiServiceRepoAnalyzer
{
    private readonly string? _token = config["Internal:Token"];
    private static readonly JsonSerializerOptions Json = new() { PropertyNameCaseInsensitive = true };
    private record ApiResult(string? Summary, List<string>? TechStack, List<string>? Strengths, List<string>? Weaknesses, List<string>? Suggestions, List<string>? InterviewTalkingPoints, Match? JdMatch);
    private record Match(int Score, List<string>? MatchedSkills, List<string>? MissingSkills);
    public async Task<RepoAnalysisAiResult> AnalyzeAsync(string digest, string jobCategory, string? jdText, CancellationToken ct = default)
    {
        using var request = new HttpRequestMessage(HttpMethod.Post, "/api/v1/analyze-repo") { Content = JsonContent.Create(new { repoDigest = digest, jobCategory, jdText }) };
        request.Headers.TryAddWithoutValidation("X-Internal-Token", _token);
        HttpResponseMessage response;
        try { response = await client.SendAsync(request, ct); }
        catch (Exception ex) when (ex is HttpRequestException or TaskCanceledException) { throw new AiServiceException("Không gọi được AIService /analyze-repo", ex); }
        if (!response.IsSuccessStatusCode) { logger.LogWarning("AI repo returned {Status}", response.StatusCode); throw new AiServiceException($"AIService /analyze-repo trả {(int)response.StatusCode}"); }
        var body = await response.Content.ReadFromJsonAsync<ApiResult>(Json, ct) ?? throw new AiServiceException("AIService /analyze-repo trả rỗng");
        return new RepoAnalysisAiResult(body.Summary ?? "", body.TechStack ?? [], body.Strengths ?? [], body.Weaknesses ?? [], body.Suggestions ?? [], body.InterviewTalkingPoints ?? [], body.JdMatch is null ? null : new CvJdMatch(body.JdMatch.Score, body.JdMatch.MatchedSkills ?? [], body.JdMatch.MissingSkills ?? []));
    }
}
