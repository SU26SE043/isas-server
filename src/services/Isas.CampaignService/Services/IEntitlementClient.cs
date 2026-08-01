using Isas.CampaignService.Models;

namespace Isas.CampaignService.Services;

public interface IEntitlementClient
{
    /// <summary>Never leaks premium access when Payment is unavailable or returns invalid data.</summary>
    Task<CampaignEntitlement> ResolveOrgAsync(Guid orgId, CancellationToken ct = default);
}
