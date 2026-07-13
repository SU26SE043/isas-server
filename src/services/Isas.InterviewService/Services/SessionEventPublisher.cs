using System.Text;
using System.Text.Json;
using Isas.InterviewService.DTOs;
using Isas.InterviewService.Services.Interfaces;
using RabbitMQ.Client;

namespace Isas.InterviewService.Services;

// Phát event session (SessionScored — E2; sau này SessionAbandoned — E3) lên RabbitMQ.
//
// Khác ScoringJobPublisher (publish thẳng vào 1 queue cho 1 consumer/worker): SessionScored có
// NHIỀU consumer độc lập (Campaign cập nhật ranking + Payment consume credit — kiến trúc §3, D10),
// nên publish qua 1 EXCHANGE topic để mỗi service tự khai queue/binding riêng của mình, không
// tranh nhau 1 queue (queue dùng chung sẽ chỉ có 1 consumer nhận được mỗi message).
//
// ⚠ Tên exchange/routing-key CHƯA có trong docs/architecture.md §6 (doc mới chỉ định nghĩa
// scoring_pipeline_queue cho job chấm) — đây là lựa chọn hợp lý khi build E2 (task chỉ yêu cầu
// publisher abstraction + message shape, verify bằng fake bus, không cần RabbitMQ thật). Khi làm
// E4 (Campaign consume) / E7 (Payment consume) cần khai queue durable bind vào exchange
// "interview.events" với routing key "session.scored" — hoặc đổi lại nếu team chốt convention khác.
//
// E3: dùng lại CHUNG exchange "interview.events" (KHÔNG tạo exchange thứ 2) — thêm routing key
// "session.abandoned" cho SessionAbandoned. Payment bind queue riêng vào cả 2 routing key.
public class SessionEventPublisher : ISessionEventPublisher
{
    private readonly ConnectionFactory _factory;
    private readonly ILogger<SessionEventPublisher> _logger;
    private const string ExchangeName = "interview.events";
    private const string SessionScoredRoutingKey = "session.scored";
    private const string SessionAbandonedRoutingKey = "session.abandoned";

    public SessionEventPublisher(IConfiguration config, ILogger<SessionEventPublisher> logger)
    {
        _logger = logger;
        _factory = new ConnectionFactory
        {
            HostName = config["RabbitMQ:HostName"] ?? "localhost",
            UserName = config["RabbitMQ:UserName"] ?? "guest",
            Password = config["RabbitMQ:Password"] ?? "guest"
        };
    }

    public async Task PublishSessionScoredAsync(SessionScoredEvent evt, CancellationToken ct = default)
    {
        try
        {
            await PublishAsync(SessionScoredRoutingKey, evt, ct);

            _logger.LogInformation(
                "Đã phát SessionScored cho session {SessionId} (campaign={CampaignId}, score={Score})",
                evt.SessionId, evt.CampaignId, evt.TotalScore);
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Thất bại khi phát SessionScored cho session {SessionId}", evt.SessionId);
            throw;
        }
    }

    // E3: session InProgress quá hạn, 0 answer -> bỏ ngang; Payment nghe để release reservation.
    public async Task PublishSessionAbandonedAsync(SessionAbandonedEvent evt, CancellationToken ct = default)
    {
        try
        {
            await PublishAsync(SessionAbandonedRoutingKey, evt, ct);

            _logger.LogInformation(
                "Đã phát SessionAbandoned cho session {SessionId} (campaign={CampaignId}, reason={Reason})",
                evt.SessionId, evt.CampaignId, evt.Reason);
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Thất bại khi phát SessionAbandoned cho session {SessionId}", evt.SessionId);
            throw;
        }
    }

    // Connection + exchange-declare + publish dùng chung cho mọi loại event session
    // (chỉ khác routing key/payload) — tránh lặp code giữa SessionScored và SessionAbandoned.
    private async Task PublishAsync<TEvent>(string routingKey, TEvent evt, CancellationToken ct)
    {
        await using var connection = await _factory.CreateConnectionAsync(ct);
        await using var channel = await connection.CreateChannelAsync(cancellationToken: ct);

        await channel.ExchangeDeclareAsync(
            exchange: ExchangeName,
            type: ExchangeType.Topic,
            durable: true,
            autoDelete: false,
            arguments: null,
            cancellationToken: ct);

        var json = JsonSerializer.Serialize(evt);
        var body = Encoding.UTF8.GetBytes(json);

        var properties = new BasicProperties
        {
            Persistent = true
        };

        await channel.BasicPublishAsync(
            exchange: ExchangeName,
            routingKey: routingKey,
            mandatory: false,
            basicProperties: properties,
            body: body,
            cancellationToken: ct);
    }
}
