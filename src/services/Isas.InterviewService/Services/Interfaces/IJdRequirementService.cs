using Isas.InterviewService.DTOs;

namespace Isas.InterviewService.Services.Interfaces;

public interface IJdRequirementService
{
    Task<JdRequirementsResponse> SuggestAsync(
        Guid candidateId, JdRequirementsRequest request, CancellationToken ct = default);
}
