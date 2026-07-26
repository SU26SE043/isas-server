using Isas.InterviewService.DTOs;
using Isas.Shared.Pagination;
namespace Isas.InterviewService.Services.Interfaces;
public interface IRepoAnalysisService
{
    Task<RepoAnalysisResponse> AnalyzeAsync(Guid candidateId, RepoAnalysisRequest request, CancellationToken ct = default);
    Task<RepoAnalysisResponse?> GetAsync(Guid candidateId, Guid id, CancellationToken ct = default);
    Task<KeysetPage<RepoAnalysisResponse>> ListAsync(Guid candidateId, string? cursor = null, int? limit = null, CancellationToken ct = default);
}
