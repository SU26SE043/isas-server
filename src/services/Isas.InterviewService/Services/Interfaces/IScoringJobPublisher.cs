using Isas.InterviewService.DTOs;

namespace Isas.InterviewService.Services.Interfaces;

public interface IScoringJobPublisher
{
    Task PublishAsync(ScoringJob job, CancellationToken ct = default);
}