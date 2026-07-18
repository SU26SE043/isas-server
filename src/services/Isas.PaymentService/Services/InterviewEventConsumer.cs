using System.Text;
using RabbitMQ.Client;
using RabbitMQ.Client.Events;

namespace Isas.PaymentService.Services
{
    /// <summary>
    /// E7: nghe event session phát bởi InterviewService (E2/E3) trên EXCHANGE topic
    /// <c>interview.events</c> và đẩy vào <see cref="ICreditEventHandler"/> để tiêu/nhả credit
    /// (payment.md §Tiêu credit: session Scored → consume; SessionAbandoned → release).
    ///
    /// Payment khai QUEUE riêng <c>payment.credit</c> (durable), bind CẢ HAI routing key
    /// <c>session.scored</c> + <c>session.abandoned</c> vào exchange dùng chung — không tranh queue
    /// với Campaign (<c>campaign.ranking</c>, E4) vì mỗi service có consumer độc lập (D10).
    ///
    /// Consumer = plumbing thuần (kết nối/khai/bind/ack) — mọi nghiệp vụ nằm trong handler để
    /// UNIT-TEST được không cần broker. Cùng pattern kết nối/reconnect với E4 SessionScoredConsumer.
    /// </summary>
    public class InterviewEventConsumer : BackgroundService
    {
        private const string ExchangeName = "interview.events";
        private const string QueueName = "payment.credit";

        // DB22 — DLX/DLQ hứng message độc (mẫu AI2 `scoring_pipeline_dlx`). Trước đây mọi lỗi đều
        // nack(requeue:true) vô điều kiện: 1 exception deterministic requeue mãi mãi và — vì prefetch=1 —
        // CHẶN TOÀN BỘ event phía sau ⇒ platform ngừng consume credit (thiếu doanh thu) VÀ ngừng release
        // (credit khách kẹt). Cả hai chiều đều mất tiền, mà triệu chứng chỉ là "hệ thống hơi chậm".
        private const string DeadLetterExchangeName = "payment.credit.dlx";
        private const string DeadLetterQueueName = "payment.credit.dead";
        private const string DeadLetterRoutingKey = "credit_dead";
        private static readonly TimeSpan ReconnectDelay = TimeSpan.FromSeconds(5);

        private readonly IConfiguration _config;
        private readonly IServiceScopeFactory _scopeFactory;
        private readonly ILogger<InterviewEventConsumer> _logger;

        public InterviewEventConsumer(
            IConfiguration config,
            IServiceScopeFactory scopeFactory,
            ILogger<InterviewEventConsumer> logger)
        {
            _config = config;
            _scopeFactory = scopeFactory;
            _logger = logger;
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
                        "InterviewEventConsumer mất kết nối RabbitMQ ({Exchange}/{Queue}) — thử lại sau {Delay}s",
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

            // Khai lại exchange (idempotent, cùng khai báo với publisher) — phòng consumer khởi động
            // trước publisher lần đầu.
            await channel.ExchangeDeclareAsync(
                exchange: ExchangeName,
                type: ExchangeType.Topic,
                durable: true,
                autoDelete: false,
                cancellationToken: stoppingToken);

            // DB22 — DLX + DLQ phải tồn tại TRƯỚC khi khai main queue trỏ tới nó.
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
                routingKey: DeadLetterRoutingKey,
                cancellationToken: stoppingToken);

            // ⚠ DEPLOY: queue `payment.credit` đang chạy LIVE được khai với arguments=null. RabbitMQ
            // KHÔNG cho sửa argument tại chỗ → redeclare kèm x-dead-letter-* sẽ ném PRECONDITION_FAILED
            // (406), đúng cái bẫy AI2 đã dính với `scoring_pipeline_queue`. Lúc apply phải drain (chờ
            // queue rỗng) → delete → để consumer khai lại, HOẶC set DLX bằng RabbitMQ policy thay vì
            // argument. Xem DEPLOYMENT.md.
            await channel.QueueDeclareAsync(
                queue: QueueName,
                durable: true,
                exclusive: false,
                autoDelete: false,
                arguments: new Dictionary<string, object?>
                {
                    ["x-dead-letter-exchange"] = DeadLetterExchangeName,
                    ["x-dead-letter-routing-key"] = DeadLetterRoutingKey
                },
                cancellationToken: stoppingToken);

            // Bind CẢ HAI key vào cùng 1 queue → 1 consumer xử lý cả consume lẫn release theo routing key.
            await channel.QueueBindAsync(
                queue: QueueName,
                exchange: ExchangeName,
                routingKey: CreditEventHandler.SessionScoredRoutingKey,
                cancellationToken: stoppingToken);

            await channel.QueueBindAsync(
                queue: QueueName,
                exchange: ExchangeName,
                routingKey: CreditEventHandler.SessionAbandonedRoutingKey,
                cancellationToken: stoppingToken);

            // Prefetch 1 → xử lý tuần tự, ack sau khi handler xong (an toàn cho idempotency absorbing).
            await channel.BasicQosAsync(0, 1, false, cancellationToken: stoppingToken);

            var consumer = new AsyncEventingBasicConsumer(channel);
            consumer.ReceivedAsync += async (_, ea) =>
            {
                try
                {
                    var json = Encoding.UTF8.GetString(ea.Body.Span);

                    using var scope = _scopeFactory.CreateScope();
                    var handler = scope.ServiceProvider.GetRequiredService<ICreditEventHandler>();
                    await handler.HandleAsync(ea.RoutingKey, json, stoppingToken);

                    await channel.BasicAckAsync(ea.DeliveryTag, multiple: false, cancellationToken: stoppingToken);
                }
                catch (Exception ex)
                {
                    // DB22 — trần retry để message độc KHÔNG chặn queue.
                    // Lần đầu (`Redelivered=false`) → requeue thử lại: đủ cho lỗi tạm thật (DB rớt/deadlock),
                    // idempotency Consume/Release (PAY-11) bảo đảm redeliver không trừ/hoàn kép.
                    // Đã redeliver mà VẪN lỗi → coi là độc → nack(requeue:false) → DLX đẩy sang DLQ để
                    // điều tra/replay. KHÔNG mất dữ liệu: message nằm nguyên trong DLQ, và Interview còn
                    // outbox dispatcher độc lập nếu cần phát lại.
                    // Cờ `Redelivered` do broker giữ nên bền qua restart consumer — không cần đếm in-memory.
                    if (ea.Redelivered)
                    {
                        _logger.LogError(ex,
                            "Event {RoutingKey} lỗi lần 2 → đẩy sang DLQ {DeadLetterQueue} (không chặn queue chính)",
                            ea.RoutingKey, DeadLetterQueueName);
                        await channel.BasicNackAsync(ea.DeliveryTag, multiple: false, requeue: false, cancellationToken: stoppingToken);
                    }
                    else
                    {
                        _logger.LogWarning(ex, "Lỗi xử lý event {RoutingKey} — nack + requeue (thử lại 1 lần)", ea.RoutingKey);
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
                "InterviewEventConsumer đang nghe {Queue} (bind {Exchange}/{ScoredKey}+{AbandonedKey})",
                QueueName, ExchangeName,
                CreditEventHandler.SessionScoredRoutingKey, CreditEventHandler.SessionAbandonedRoutingKey);

            // Giữ task sống tới khi bị huỷ; channel/connection rớt → exception ném ra ngoài, ExecuteAsync reconnect.
            await Task.Delay(Timeout.Infinite, stoppingToken);
        }
    }
}
