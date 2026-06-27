using Isas.InterviewService.DTOs;

namespace Isas.InterviewService.Services.Interfaces;

public interface IPracticeService
{
    Task<PracticeSessionResponse> CreateSessionAsync(
        Guid candidateId, CreatePracticeSessionRequest request, CancellationToken ct = default);

    // I1: tạo session B2B (gắn campaign_id) + materialize tiêu chí campaign → rubric_criteria(campaign_id).
    Task<PracticeSessionResponse> CreateCampaignSessionAsync(
        Guid candidateId, CreateCampaignSessionRequest request, CancellationToken ct = default);

    Task SubmitSessionAsync(
        Guid candidateId, Guid sessionId, CancellationToken ct = default);

    Task<PracticeSessionResponse?> GetSessionAsync(
        Guid candidateId, Guid sessionId, CancellationToken ct = default);

    Task<IReadOnlyList<PracticeSessionSummary>> GetHistoryAsync(
        Guid candidateId, CancellationToken ct = default);
}