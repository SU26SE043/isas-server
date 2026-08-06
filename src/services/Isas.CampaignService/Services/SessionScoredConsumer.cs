using System.Text;
using System.Text.Json;
using Isas.CampaignService.DTOs;
using RabbitMQ.Client;
using RabbitMQ.Client.Events;

namespace Isas.CampaignService.Services
{
    /// <summary>
    /// E4: nghe event <c>SessionScored</c> phát bởi InterviewService (interview.md §Sự kiện phát ra)
    /// và đẩy vào <see cref="IRankingEventHandler"/> để upsert <c>campaign_rankings</c>.
    ///
    /// Publisher (InterviewService.SessionEventPublisher) phát lên EXCHANGE topic
    /// <c>interview.events</c> (routing key <c>session.scored</c>) vì event này có NHIỀU consumer
    /// độc lập (Campaign + Payment — D10) — mỗi bên tự khai QUEUE riêng bind vào exchange, không
    /// tranh nhau 1 queue dùng chung (khác <c>scoring_pipeline_queue</c>, vốn chỉ 1 consumer/worker).
    /// Queue riêng của Campaign: <c>campaign.ranking</c> (durable, bind key <c>session.scored</c>).
    ///
    /// Cùng pattern kết nối RabbitMQ với InvitationEmailPublisher/SessionEventPublisher
    /// (<c>RabbitMQ:HostName/UserName/Password</c> trong appsettings.json) — chiều publish thay vì
    /// consume nên dùng <see cref="AsyncEventingBasicConsumer"/> + manual ack, tự reconnect khi rớt kết nối.
    /// </summary>
    public class SessionScoredConsumer : BackgroundService
    {
        private const string ExchangeName = "interview.events";
        private const string ScoredRoutingKey = "session.scored";
        private const string AbandonedRoutingKey = "session.abandoned";
        private const string QueueName = "campaign.ranking";
        // Queue chính đã tồn tại trên production không có arguments. DLX phải được gắn bằng
        // RabbitMQ policy (xem DEPLOYMENT.md), tuyệt đối không thêm arguments khi redeclare.
        private const string DeadLetterExchangeName = "campaign.ranking.dlx";
        private const string DeadLetterQueueName = "campaign.ranking.dead";
        private static readonly TimeSpan ReconnectDelay = TimeSpan.FromSeconds(5);

        private static readonly JsonSerializerOptions JsonOptions = new()
        {
            PropertyNameCaseInsensitive = true
        };

        private readonly IConfiguration _config;
        private readonly IServiceScopeFactory _scopeFactory;
        private readonly ILogger<SessionScoredConsumer> _logger;
        private readonly bool _releaseOnAbandoned;

        public SessionScoredConsumer(
            IConfiguration config,
            IServiceScopeFactory scopeFactory,
            ILogger<SessionScoredConsumer> logger)
        {
            _config = config;
            _scopeFactory = scopeFactory;
            _logger = logger;
            _releaseOnAbandoned = config.GetValue<bool>("Membership:ReleaseOnAbandoned");
        }

        protected override async Task ExecuteAsync(CancellationToken stoppingToken)
        {
            while (!stoppingToken.IsCancellationRequested)
            {
                try
                {
                    await RunConsumerAsync(stoppingToken);
                }
                catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested)
                {
                    break;
                }
                catch (Exception ex)
                {
                    _logger.LogError(ex,
                        "SessionScoredConsumer mất kết nối RabbitMQ ({Exchange}/{Queue}) — thử lại sau {Delay}s",
                        ExchangeName, QueueName, ReconnectDelay.TotalSeconds);

                    try
                    {
                        await Task.Delay(ReconnectDelay, stoppingToken);
                    }
                    catch (OperationCanceledException)
                    {
                        break;
                    }
                }
            }
        }

        private async Task RunConsumerAsync(CancellationToken stoppingToken)
        {
            var factory = new ConnectionFactory
            {
                HostName = _config["RabbitMQ:HostName"] ?? "localhost",
                UserName = _config["RabbitMQ:UserName"] ?? "guest",
                Password = _config["RabbitMQ:Password"] ?? "guest"
            };

            await using var connection = await factory.CreateConnectionAsync(stoppingToken);
            await using var channel = await connection.CreateChannelAsync(cancellationToken: stoppingToken);

            // Khai lại exchange (idempotent, cùng khai báo với publisher) — Campaign không sở hữu
            // exchange nhưng cần đảm bảo nó tồn tại trước khi bind queue, phòng consumer khởi động
            // trước publisher lần đầu.
            await channel.ExchangeDeclareAsync(
                exchange: ExchangeName,
                type: ExchangeType.Topic,
                durable: true,
                autoDelete: false,
                cancellationToken: stoppingToken);

            await channel.QueueDeclareAsync(
                queue: QueueName,
                durable: true,
                exclusive: false,
                autoDelete: false,
                cancellationToken: stoppingToken);

            await channel.QueueBindAsync(
                queue: QueueName,
                exchange: ExchangeName,
                routingKey: ScoredRoutingKey,
                cancellationToken: stoppingToken);

            // Chỉ khai đối tượng MỚI: đổi arguments của QueueName đang sống sẽ gây 406
            // PRECONDITION_FAILED. Policy ops sẽ route các BasicNack(requeue:false) vào đây.
            await channel.ExchangeDeclareAsync(
                exchange: DeadLetterExchangeName,
                type: ExchangeType.Direct,
                durable: true,
                autoDelete: false,
                cancellationToken: stoppingToken);
            await channel.QueueDeclareAsync(
                queue: DeadLetterQueueName,
                durable: true,
                exclusive: false,
                autoDelete: false,
                cancellationToken: stoppingToken);
            await channel.QueueBindAsync(
                queue: DeadLetterQueueName,
                exchange: DeadLetterExchangeName,
                routingKey: QueueName,
                cancellationToken: stoppingToken);
            if (_releaseOnAbandoned)
                await channel.QueueBindAsync(queue: QueueName, exchange: ExchangeName, routingKey: AbandonedRoutingKey,
                    cancellationToken: stoppingToken);

            // Prefetch 1 → xử lý tuần tự, ack sau khi upsert xong (an toàn cho idempotency find-or-update).
            await channel.BasicQosAsync(0, 1, false, cancellationToken: stoppingToken);

            var consumer = new AsyncEventingBasicConsumer(channel);
            consumer.ReceivedAsync += async (_, ea) =>
            {
                try
                {
                    var json = Encoding.UTF8.GetString(ea.Body.Span);
                    using var scope = _scopeFactory.CreateScope();
                    var handler = scope.ServiceProvider.GetRequiredService<IRankingEventHandler>();
                    if (ea.RoutingKey == ScoredRoutingKey)
                    {
                        var evt = JsonSerializer.Deserialize<SessionScoredMessage>(json, JsonOptions);
                        if (evt is null) throw new JsonException("SessionScored message rỗng.");
                        await handler.HandleSessionScoredAsync(evt, stoppingToken);
                    }
                    else if (_releaseOnAbandoned && ea.RoutingKey == AbandonedRoutingKey)
                    {
                        var evt = JsonSerializer.Deserialize<SessionAbandonedMessage>(json, JsonOptions);
                        if (evt is null) throw new JsonException("SessionAbandoned message rỗng.");
                        await handler.HandleSessionAbandonedAsync(evt, stoppingToken);
                    }
                    else if (!_releaseOnAbandoned && ea.RoutingKey == AbandonedRoutingKey)
                    {
                        _logger.LogDebug(
                            "Bỏ qua event {RoutingKey} vì Membership:ReleaseOnAbandoned=false",
                            ea.RoutingKey);
                    }

                    await channel.BasicAckAsync(ea.DeliveryTag, multiple: false, cancellationToken: stoppingToken);
                }
                catch (Exception ex)
                {
                    if (ea.Redelivered)
                    {
                        _logger.LogError(ex, "Event {RoutingKey} lỗi lần 2 — bỏ khỏi queue chính", ea.RoutingKey);
                        await channel.BasicNackAsync(ea.DeliveryTag, multiple: false, requeue: false, cancellationToken: stoppingToken);
                    }
                    else
                    {
                        _logger.LogWarning(ex, "Lỗi xử lý event {RoutingKey} — thử lại một lần", ea.RoutingKey);
                        await channel.BasicNackAsync(ea.DeliveryTag, multiple: false, requeue: true, cancellationToken: stoppingToken);
                    }
                }
            };

            await channel.BasicConsumeAsync(
                queue: QueueName,
                autoAck: false,
                consumer: consumer,
                cancellationToken: stoppingToken);

            _logger.LogInformation(
                "SessionScoredConsumer đang nghe {Queue} (bind {Exchange}/{ScoredKey}+{AbandonedKey})",
                QueueName, ExchangeName, ScoredRoutingKey, AbandonedRoutingKey);

            // Giữ task sống tới khi bị huỷ; nếu channel/connection rớt, exception sẽ ném ra ngoài
            // và ExecuteAsync sẽ reconnect.
            await Task.Delay(Timeout.Infinite, stoppingToken);
        }
    }
}
