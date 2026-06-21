using System.Text;
using System.Text.Json;
using Isas.InterviewService.DTOs;
using Isas.InterviewService.Services.Interfaces;
using RabbitMQ.Client;

namespace Isas.InterviewService.Services;

public class ScoringJobPublisher : IScoringJobPublisher
{
    private readonly ConnectionFactory _factory;
    private readonly ILogger<ScoringJobPublisher> _logger;
    private const string QueueName = "scoring_pipeline_queue";

    public ScoringJobPublisher(IConfiguration config, ILogger<ScoringJobPublisher> logger)
    {
        _logger = logger;
        _factory = new ConnectionFactory
        {
            HostName = config["RabbitMQ:HostName"] ?? "localhost",
            UserName = config["RabbitMQ:UserName"] ?? "guest",
            Password = config["RabbitMQ:Password"] ?? "guest"
        };
    }

    public async Task PublishAsync(ScoringJob job, CancellationToken ct = default)
    {
        try
        {
            // Bản mới bắt buộc await khi tạo Connection và Channel
            await using var connection = await _factory.CreateConnectionAsync(ct);
            await using var channel = await connection.CreateChannelAsync(cancellationToken: ct);

            // Tất cả method chuyển sang đuôi Async và nhận CancellationToken ở cuối
            await channel.QueueDeclareAsync(
                queue: QueueName,
                durable: true,
                exclusive: false,
                autoDelete: false,
                arguments: null,
                cancellationToken: ct);

            var json = JsonSerializer.Serialize(job);
            var body = Encoding.UTF8.GetBytes(json);

            // Cấu hình Persistent kiểu mới trực tiếp qua object initializer
            var properties = new BasicProperties
            {
                Persistent = true 
            };

            // Gọi hàm publish bất đồng bộ băm thẳng vào Queue
            await channel.BasicPublishAsync(
                exchange: string.Empty,
                routingKey: QueueName,
                mandatory: false,
                basicProperties: properties,
                body: body,
                cancellationToken: ct);

            _logger.LogInformation("Đã đẩy job chấm điểm cho Answer {AnswerId} bằng Modern Async API", job.AnswerId);
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Thất bại khi đẩy job cho Answer {AnswerId} qua RabbitMQ mới", job.AnswerId);
            throw;
        }
    }
}