using System.Text;
using System.Text.Json;
using RabbitMQ.Client;
using RabbitMQ.Client.Events;

namespace Isas.CampaignService.Services
{
    /// <summary>
    /// Tiêu thụ <c>campaign_invitation_email_queue</c> (do <see cref="InvitationEmailPublisher"/> đẩy)
    /// và gửi email mời (magic-link) qua SMTP.
    ///
    /// Đây là work-queue direct-to-queue trên DEFAULT exchange (routingKey = tên queue) → chỉ cần
    /// <c>QueueDeclareAsync</c> KHỚP publisher, KHÔNG khai exchange/bind (khác <see cref="SessionScoredConsumer"/>
    /// vốn bind vào topic exchange <c>interview.events</c>).
    ///
    /// Cùng pattern connect/reconnect + manual ack với <see cref="SessionScoredConsumer"/>:
    /// prefetch 1, <see cref="AsyncEventingBasicConsumer"/>, ack sau khi gửi xong,
    /// nack-requeue khi lỗi (email tạm lỗi → thử lại). Sender resolve theo scope/mỗi message.
    /// </summary>
    public class InvitationEmailConsumer : BackgroundService
    {
        private const string QueueName = "campaign_invitation_email_queue";
        private static readonly TimeSpan ReconnectDelay = TimeSpan.FromSeconds(5);

        private static readonly JsonSerializerOptions JsonOptions = new()
        {
            PropertyNameCaseInsensitive = true
        };

        private readonly IConfiguration _config;
        private readonly IServiceScopeFactory _scopeFactory;
        private readonly ILogger<InvitationEmailConsumer> _logger;

        public InvitationEmailConsumer(
            IConfiguration config,
            IServiceScopeFactory scopeFactory,
            ILogger<InvitationEmailConsumer> logger)
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
                        "InvitationEmailConsumer mất kết nối RabbitMQ ({Queue}) — thử lại sau {Delay}s",
                        QueueName, ReconnectDelay.TotalSeconds);

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

            // Khai queue KHỚP publisher (default exchange, direct-to-queue) — không exchange/bind.
            await channel.QueueDeclareAsync(
                queue: QueueName,
                durable: true,
                exclusive: false,
                autoDelete: false,
                arguments: null,
                cancellationToken: stoppingToken);

            // Prefetch 1 → gửi tuần tự, ack sau khi SMTP gửi xong.
            await channel.BasicQosAsync(0, 1, false, cancellationToken: stoppingToken);

            var consumer = new AsyncEventingBasicConsumer(channel);
            consumer.ReceivedAsync += async (_, ea) =>
            {
                try
                {
                    using var scope = _scopeFactory.CreateScope();
                    var sender = scope.ServiceProvider.GetRequiredService<ICampaignEmailSender>();
                    await ProcessMessageAsync(ea.Body.ToArray(), sender, stoppingToken);

                    await channel.BasicAckAsync(ea.DeliveryTag, multiple: false, cancellationToken: stoppingToken);
                }
                catch (Exception ex)
                {
                    _logger.LogError(ex, "Lỗi gửi email mời — nack + requeue");
                    await channel.BasicNackAsync(ea.DeliveryTag, multiple: false, requeue: true, cancellationToken: stoppingToken);
                }
            };

            await channel.BasicConsumeAsync(
                queue: QueueName,
                autoAck: false,
                consumer: consumer,
                cancellationToken: stoppingToken);

            _logger.LogInformation("InvitationEmailConsumer đang nghe {Queue}", QueueName);

            // Giữ task sống tới khi bị huỷ; channel/connection rớt → exception ném ra ExecuteAsync để reconnect.
            await Task.Delay(Timeout.Infinite, stoppingToken);
        }

        /// <summary>
        /// Logic 1 message: deserialize job → compose magic-link → gọi sender.
        /// Tách ra để unit-test không cần broker/SMTP (mock <see cref="ICampaignEmailSender"/>).
        /// </summary>
        public async Task ProcessMessageAsync(byte[] body, ICampaignEmailSender sender, CancellationToken ct)
        {
            var job = JsonSerializer.Deserialize<InvitationEmailJob>(body, JsonOptions);
            if (job is null)
            {
                _logger.LogWarning(
                    "InvitationEmailJob rỗng/không deserialize được — bỏ qua: {Json}",
                    Encoding.UTF8.GetString(body));
                return;
            }

            var link = BuildJoinLink(job.Token);

            await sender.SendInvitationEmailAsync(job.Email, job.CampaignTitle, link, job.ExpiresAt, ct);

            _logger.LogInformation(
                "Đã gửi email mời cho Invitation {InvitationId} ({Email})",
                job.InvitationId, job.Email);
        }

        /// <summary>
        /// Magic-link tới trang join ứng viên qua gateway: <c>{baseUrl}/invitations/{token}</c>.
        /// baseUrl = <c>Invitation:BaseUrl</c> ?? <c>Gateway:Url</c> ?? rỗng (bỏ dấu '/' cuối tránh '//').
        /// </summary>
        private string BuildJoinLink(string token)
        {
            var baseUrl = (_config["Invitation:BaseUrl"] ?? _config["Gateway:Url"] ?? string.Empty)
                .TrimEnd('/');
            return $"{baseUrl}/invitations/{token}";
        }
    }
}
