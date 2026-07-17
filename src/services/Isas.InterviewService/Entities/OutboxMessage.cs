using System.Text.Json;
using Isas.InterviewService.DTOs;

namespace Isas.InterviewService.Entities;

// DB2 — Transactional Outbox cho settlement-event (SessionScored/SessionAbandoned). Row được ghi CÙNG
// transaction với việc đổi trạng thái session (đóng session) → không mất event khi RabbitMQ chết lúc
// đóng session. OutboxDispatcher (BackgroundService) quét row `published_at IS NULL`, publish lên
// exchange "interview.events" (routing key = Type), rồi set published_at. At-least-once: publish lỗi →
// giữ published_at null + Attempts++ → vòng sau gửi lại (consumer Payment idempotent theo session_id).
//
// Thay cho "publish best-effort SAU SaveChanges" cũ (mất event khi broker down) + SettlementReconciler
// (chỉ backfill B2C, bỏ sót B2B + generation_failed). Outbox phủ CẢ B2C, B2B và generation_failed.
public class OutboxMessage
{
    // Routing key = kiểu event (khớp SessionEventPublisher/Payment CreditEventHandler).
    public const string SessionScoredType = "session.scored";
    public const string SessionAbandonedType = "session.abandoned";

    // Message-id ổn định: dùng làm BasicProperties.MessageId khi publish (khoá idempotency phía consumer).
    public Guid Id { get; set; } = Guid.NewGuid();

    // Routing key trên exchange "interview.events" ("session.scored" / "session.abandoned").
    public string Type { get; set; } = default!;

    // Payload JSON NGUYÊN của SessionScoredEvent/SessionAbandonedEvent — publish y hệt (không reconstruct).
    public string Payload { get; set; } = default!;

    // Ref session (quan sát/đối soát; KHÔNG FK).
    public Guid SessionId { get; set; }

    // Thời điểm event xảy ra (lúc đóng session) — dispatcher order theo cột này để giữ thứ tự phát.
    public DateTime OccurredAt { get; set; } = DateTime.UtcNow;

    // null = chưa publish (dispatcher sẽ quét); set khi publish thành công (idempotent, vòng sau bỏ qua).
    public DateTime? PublishedAt { get; set; }

    // Số lần thử publish (tăng mỗi lần broker down) — quan sát/chẩn đoán.
    public int Attempts { get; set; }

    // Payload phải GIỮ NGUYÊN ngữ nghĩa event (đặc biệt B2B TotalScore weighted + Reason gốc). Serialize
    // bằng options mặc định (khớp SessionEventPublisher cũ; Payment deserialize case-insensitive nên
    // casing không ảnh hưởng tương thích).
    public static OutboxMessage ForScored(SessionScoredEvent evt) => new()
    {
        Type = SessionScoredType,
        Payload = JsonSerializer.Serialize(evt),
        SessionId = evt.SessionId,
        OccurredAt = evt.ScoredAt
    };

    public static OutboxMessage ForAbandoned(SessionAbandonedEvent evt) => new()
    {
        Type = SessionAbandonedType,
        Payload = JsonSerializer.Serialize(evt),
        SessionId = evt.SessionId,
        OccurredAt = evt.AbandonedAt
    };
}
