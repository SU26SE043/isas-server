using Isas.InterviewService.DTOs;

namespace Isas.InterviewService.Services.Interfaces;

// Transport thuần: chỉ đẩy message (SessionScored/SessionAbandoned) lên RabbitMQ — không có
// business logic (giống IScoringJobPublisher; nơi gọi tự build message rồi mới publish).
public interface ISessionEventPublisher
{
    Task PublishSessionScoredAsync(SessionScoredEvent evt, CancellationToken ct = default);

    // E3: session InProgress quá hạn, 0 answer -> bỏ ngang. Payment nghe để release reservation.
    Task PublishSessionAbandonedAsync(SessionAbandonedEvent evt, CancellationToken ct = default);
}
