namespace Isas.InterviewService.Services.Interfaces;

// Transport thuần: đẩy 1 message đã-serialize (từ outbox_messages) lên exchange "interview.events".
// DB2 — không còn method typed (SessionScored/SessionAbandoned) publish trực tiếp: mọi settlement-event
// đi qua Transactional Outbox → OutboxDispatcher là ĐƯỜNG DUY NHẤT publish (tránh double-publish). Nơi
// đóng session chỉ GHI outbox-row (cùng transaction với state-flip), không publish.
public interface ISessionEventPublisher
{
    // Publish payload NGUYÊN (không reconstruct) lên "interview.events" với routing key = message Type.
    // messageId → BasicProperties.MessageId (khoá idempotency phía consumer). Lỗi (broker down) → ném ra
    // để dispatcher giữ published_at null + Attempts++ (gửi lại vòng sau, event không mất).
    Task PublishRawAsync(string routingKey, string payloadJson, string messageId, CancellationToken ct = default);
}
