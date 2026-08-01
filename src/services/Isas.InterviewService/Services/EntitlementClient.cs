using System.Net.Http.Json;
using System.Text.Json;
using Isas.InterviewService.Models;
using Isas.InterviewService.Services.Interfaces;

namespace Isas.InterviewService.Services;

public sealed class EntitlementClient(HttpClient client, IConfiguration config, ILogger<EntitlementClient> logger) : IEntitlementClient
{
    private static readonly JsonSerializerOptions Json = new() { PropertyNameCaseInsensitive = true };
    private readonly string? _internalToken = config["Internal:Token"];

    private sealed record ApiResponse(string? Source, string? TierCode, int TierRank, string? EntitlementSnapshot);
    private sealed record Features(bool AdaptiveEnabled, int MaxQuestions, int MaxFollowUps, bool GroundingEnabled,
        int SelfConsistencyN, bool CvAnalysisIncluded, bool RepoAnalysisIncluded, bool RoadmapEnabled);

    public async Task<EntitlementSnapshot> ResolveUserAsync(Guid candidateId, CancellationToken ct = default)
    {
        try
        {
            using var request = new HttpRequestMessage(HttpMethod.Get,
                $"/internal/entitlements?ownerType=User&ownerId={candidateId}");
            request.Headers.TryAddWithoutValidation("X-Internal-Token", _internalToken);
            using var response = await client.SendAsync(request, ct);
            if (!response.IsSuccessStatusCode)
            {
                logger.LogWarning("Payment entitlement returned {StatusCode}; using Free entitlement", response.StatusCode);
                return EntitlementSnapshot.Free;
            }
            var body = await response.Content.ReadFromJsonAsync<ApiResponse>(Json, ct);
            if (body?.TierCode is null || string.IsNullOrWhiteSpace(body.EntitlementSnapshot))
                throw new JsonException("Payment entitlement response is incomplete.");
            var features = JsonSerializer.Deserialize<Features>(body.EntitlementSnapshot, Json);
            if (features is null) throw new JsonException("Payment entitlement snapshot is invalid.");
            return new EntitlementSnapshot(body.Source ?? "resolved", body.TierCode, body.TierRank,
                features.AdaptiveEnabled, Math.Clamp(features.MaxQuestions, 0, 20), Math.Max(0, features.MaxFollowUps),
                features.GroundingEnabled, Math.Max(1, features.SelfConsistencyN), features.CvAnalysisIncluded,
                features.RepoAnalysisIncluded, features.RoadmapEnabled);
        }
        catch (Exception ex) when (ex is HttpRequestException or TaskCanceledException or JsonException)
        {
            logger.LogWarning(ex, "Payment entitlement unavailable or invalid; using Free entitlement");
            return EntitlementSnapshot.Free;
        }
    }
}
