using System.Text;
using Isas.InterviewService.Services.Interfaces;
using RabbitMQ.Client;

namespace Isas.InterviewService.Services;

// Transport RabbitMQ cho settlement-event (SessionScored/SessionAbandoned) — publish 1 payload đã
// serialize lên exchange topic "interview.events" với routing key = message Type.
//
// Khác ScoringJobPublisher (publish thẳng vào 1 queue cho 1 consumer/worker): settlement-event có
// NHIỀU consumer độc lập (Campaign cập nhật ranking + Payment consume/release credit — kiến trúc §3,
// D10), nên publish qua 1 EXCHANGE topic để mỗi service tự khai queue/binding riêng, không tranh 1 queue.
//
// DB2 — CHỈ OutboxDispatcher gọi publisher này (row từ outbox_messages). Nơi đóng session KHÔNG publish
// trực tiếp nữa (ghi outbox-row cùng transaction với state-flip) → đường publish duy nhất, không double.
public class SessionEventPublisher : ISessionEventPublisher
{
    private readonly ConnectionFactory _factory;
    private readonly ILogger<SessionEventPublisher> _logger;
    private const string ExchangeName = "interview.events";

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

    // Publish payload NGUYÊN từ outbox-row. messageId → BasicProperties.MessageId (khoá idempotency phía
    // consumer). Lỗi (broker down) → ném ra để OutboxDispatcher giữ published_at null + Attempts++.
    public async Task PublishRawAsync(
        string routingKey, string payloadJson, string messageId, CancellationToken ct = default)
    {
        try
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

            var body = Encoding.UTF8.GetBytes(payloadJson);

            var properties = new BasicProperties
            {
                Persistent = true,
                MessageId = messageId
            };

            await channel.BasicPublishAsync(
                exchange: ExchangeName,
                routingKey: routingKey,
                mandatory: false,
                basicProperties: properties,
                body: body,
                cancellationToken: ct);

            _logger.LogInformation(
                "Đã phát settlement-event {RoutingKey} (messageId={MessageId})", routingKey, messageId);
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Thất bại khi phát settlement-event {RoutingKey} (messageId={MessageId})",
                routingKey, messageId);
            throw;
        }
    }
}
