using System.Text;
using System.Text.Json;
using RabbitMQ.Client;

namespace Isas.CampaignService.Services
{
    /// <summary>
    /// C14: đẩy job sàng CV (AI chấm khớp) vào RabbitMQ <c>cv_screening_queue</c>.
    /// Cùng pattern <see cref="InvitationEmailPublisher"/> (RabbitMQ.Client v7 async). Worker Python
    /// (AIService) tiêu thụ queue này KHÔNG thuộc phạm vi C14 — xem [ai.md] §Pipeline sàng CV B2B.
    /// </summary>
    public class CvScreeningPublisher : ICvScreeningPublisher
    {
        private readonly ConnectionFactory _factory;
        private readonly ILogger<CvScreeningPublisher> _logger;
        private const string QueueName = "cv_screening_queue";

        public CvScreeningPublisher(IConfiguration config, ILogger<CvScreeningPublisher> logger)
        {
            _logger = logger;
            _factory = new ConnectionFactory
            {
                HostName = config["RabbitMQ:HostName"] ?? "localhost",
                UserName = config["RabbitMQ:UserName"] ?? "guest",
                Password = config["RabbitMQ:Password"] ?? "guest"
            };
        }

        public async Task PublishAsync(CvScreeningJob job, CancellationToken ct = default)
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

                _logger.LogInformation("Đã đẩy job sàng CV cho candidate {CandidateId}", job.CandidateId);
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Thất bại khi đẩy job sàng CV cho candidate {CandidateId} qua RabbitMQ", job.CandidateId);
                throw;
            }
        }
    }
}
