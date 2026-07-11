using Isas.InterviewService.DTOs;

namespace Isas.InterviewService.Services.Interfaces;

// Transport thuần: chỉ đẩy message SessionScored lên RabbitMQ — không có business logic
// (giống IScoringJobPublisher; nơi gọi tự build message rồi mới publish).
public interface ISessionEventPublisher
{
    Task PublishSessionScoredAsync(SessionScoredEvent evt, CancellationToken ct = default);
}
