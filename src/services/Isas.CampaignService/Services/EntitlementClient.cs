using System.Net.Http.Json;
using System.Text.Json;
using Isas.CampaignService.Models;
using Microsoft.Extensions.Caching.Memory;
using Microsoft.Extensions.Options;

namespace Isas.CampaignService.Services;

public sealed class EntitlementClient(
    HttpClient client, IConfiguration config, IMemoryCache cache, ILogger<EntitlementClient> logger,
    IOptions<TieringSettings> tiering) : IEntitlementClient
{
    private const string CachePrefix = "campaign-entitlement-org:";
    private static readonly JsonSerializerOptions Json = new() { PropertyNameCaseInsensitive = true };
    private readonly string? _internalToken = config["Internal:Token"];
    private sealed record ApiResponse(string? Source, string? TierCode, int TierRank, string? EntitlementSnapshot);
    private sealed record Features(int? MaxActiveCampaigns, int? MaxCandidatesCap, bool AdaptiveEnabled,
        bool GroundingEnabled, bool PostpaidEligible);

    public Task<CampaignEntitlement> ResolveOrgAsync(Guid orgId, CancellationToken ct = default)
    {
        if (!tiering.Value.Enabled)
            return Task.FromResult(CampaignEntitlement.Legacy);

        return cache.GetOrCreateAsync(CachePrefix + orgId, async entry =>
        {
            entry.AbsoluteExpirationRelativeToNow = TimeSpan.FromSeconds(90);
            try
            {
                using var request = new HttpRequestMessage(HttpMethod.Get,
                    $"/internal/entitlements?ownerType=Org&ownerId={orgId}");
                request.Headers.TryAddWithoutValidation("X-Internal-Token", _internalToken);
                using var response = await client.SendAsync(request, ct);
                if (!response.IsSuccessStatusCode)
                {
                    logger.LogWarning("Payment entitlement returned {StatusCode} for org {OrgId}; using Starter", response.StatusCode, orgId);
                    return CampaignEntitlement.Starter;
                }

                var body = await response.Content.ReadFromJsonAsync<ApiResponse>(Json, ct);
                if (body?.TierCode is null || string.IsNullOrWhiteSpace(body.EntitlementSnapshot))
                    throw new JsonException("Payment entitlement response is incomplete.");
                var features = JsonSerializer.Deserialize<Features>(body.EntitlementSnapshot, Json)
                    ?? throw new JsonException("Payment entitlement snapshot is invalid.");
                if (features.MaxActiveCampaigns is < 1 || features.MaxCandidatesCap is < 1)
                    throw new JsonException("Payment B2B caps are invalid.");
                return new CampaignEntitlement(body.Source ?? "resolved", body.TierCode, body.TierRank,
                    features.MaxActiveCampaigns ?? int.MaxValue, features.MaxCandidatesCap ?? int.MaxValue,
                    features.AdaptiveEnabled, features.GroundingEnabled, features.PostpaidEligible);
            }
            catch (Exception ex) when (ex is HttpRequestException or TaskCanceledException or JsonException)
            {
                logger.LogWarning(ex, "Payment entitlement unavailable or invalid for org {OrgId}; using Starter", orgId);
                return CampaignEntitlement.Starter;
            }
        })!;
    }
}
