using System.Text;
using System.Text.Json;
using RabbitMQ.Client;

namespace Isas.CampaignService.Services
{
    /// <summary>
    /// D1: đẩy job gửi email mời (magic-link) vào RabbitMQ.
    /// Cùng pattern InterviewService.ScoringJobPublisher — worker tiêu thụ queue này KHÔNG thuộc phạm vi D1.
    /// </summary>
    public class InvitationEmailPublisher : IInvitationEmailPublisher
    {
        private readonly ConnectionFactory _factory;
        private readonly ILogger<InvitationEmailPublisher> _logger;
        private const string QueueName = "campaign_invitation_email_queue";

        public InvitationEmailPublisher(IConfiguration config, ILogger<InvitationEmailPublisher> logger)
        {
            _logger = logger;
            _factory = new ConnectionFactory
            {
                HostName = config["RabbitMQ:HostName"] ?? "localhost",
                UserName = config["RabbitMQ:UserName"] ?? "guest",
                Password = config["RabbitMQ:Password"] ?? "guest"
            };
        }

        public async Task PublishAsync(InvitationEmailJob job, CancellationToken ct = default)
        {
            try
            {
                await using var connection = await _factory.CreateConnectionAsync(ct);
                await using var channel = await connection.CreateChannelAsync(cancellationToken: ct);

                await channel.QueueDeclareAsync(
                    queue: QueueName,
                    durable: true,
                    exclusive: false,
                    autoDelete: false,
                    arguments: null,
                    cancellationToken: ct);

                var json = JsonSerializer.Serialize(job);
                var body = Encoding.UTF8.GetBytes(json);

                var properties = new BasicProperties
                {
                    Persistent = true
                };

                await channel.BasicPublishAsync(
                    exchange: string.Empty,
                    routingKey: QueueName,
                    mandatory: false,
                    basicProperties: properties,
                    body: body,
                    cancellationToken: ct);

                _logger.LogInformation("Đã đẩy job email mời cho Invitation {InvitationId} ({Email})", job.InvitationId, job.Email);
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Thất bại khi đẩy job email mời cho Invitation {InvitationId} qua RabbitMQ", job.InvitationId);
                throw;
            }
        }
    }
}
