using Isas.CampaignService.DTOs;

namespace Isas.CampaignService.Services
{
    /// <summary>
    /// E4: xử lý event <c>SessionScored</c> → upsert <c>campaign_rankings</c>.
    /// Tách khỏi việc consume RabbitMQ (SessionScoredConsumer) để test được bằng
    /// fake/in-memory bus, không cần RabbitMQ thật.
    /// </summary>
    public interface IRankingEventHandler
    {
        Task HandleSessionScoredAsync(SessionScoredMessage evt, CancellationToken ct = default);
    }
}
