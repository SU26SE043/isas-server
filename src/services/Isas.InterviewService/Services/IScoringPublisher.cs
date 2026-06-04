namespace Isas.InterviewService.Services;

public interface IScoringPublisher
{
    Task PublishAsync(Guid sessionId, CancellationToken ct = default);
}