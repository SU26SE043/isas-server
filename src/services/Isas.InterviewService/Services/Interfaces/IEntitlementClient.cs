using Isas.InterviewService.Models;

namespace Isas.InterviewService.Services.Interfaces;

public interface IEntitlementClient
{
    /// <summary>Never throws for a remote Payment failure; those cases resolve to Free.</summary>
    Task<EntitlementSnapshot> ResolveUserAsync(Guid candidateId, CancellationToken ct = default);
}
