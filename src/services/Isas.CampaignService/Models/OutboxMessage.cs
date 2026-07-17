using System.Text.Json;
using Isas.CampaignService.Services;

namespace Isas.CampaignService.Models
{
    // DB2b — Transactional Outbox cho invitation-email. Row được ghi CÙNG transaction với việc tạo
    // invitation (CreateInvitations / InviteShortlisted / ReissueInvitation) → KHÔNG mất mail khi RabbitMQ
    // chết lúc tạo lời mời. OutboxDispatcher (BackgroundService) quét row `published_at IS NULL`, publish
    // payload NGUYÊN lên queue campaign_invitation_email_queue (MessageId = Id), rồi set published_at.
    // At-least-once: publish lỗi (broker down) → giữ published_at null + Attempts++ → vòng sau gửi lại.
    //
    // Thay cho "publish best-effort SAU SaveChanges" cũ (dual-write: mất mail khi broker down giữa 2
    // SaveChanges). Consumer idempotent theo email_sent_at (redeliver → không gửi trùng).
    public class OutboxMessage
    {
        // Loại message (invitation-email). Queue transport 1-đích nên đây là hằng phân loại/đối soát.
        public const string InvitationEmailType = "invitation.email";

        // Message-id ổn định: dùng làm BasicProperties.MessageId khi publish (đối soát/quan sát phía queue).
        public Guid Id { get; set; } = Guid.NewGuid();

        // Loại message ("invitation.email").
        public string Type { get; set; } = default!;

        // Payload JSON NGUYÊN của InvitationEmailJob — publish y hệt (không reconstruct).
        public string Payload { get; set; } = default!;

        // Ref lỏng (quan sát/đối soát; KHÔNG FK xuyên bảng).
        public Guid InvitationId { get; set; }
        public Guid CampaignId { get; set; }

        // Thời điểm tạo lời mời — dispatcher order theo cột này để giữ thứ tự phát.
        public DateTime OccurredAt { get; set; } = DateTime.UtcNow;

        // null = chưa publish (dispatcher sẽ quét); set khi publish thành công (vòng sau bỏ qua).
        public DateTime? PublishedAt { get; set; }

        // Số lần thử publish (tăng mỗi lần broker down) — quan sát/chẩn đoán.
        public int Attempts { get; set; }

        // Serialize job bằng options mặc định (khớp InvitationEmailPublisher.PublishAsync cũ; consumer
        // deserialize case-insensitive nên casing không ảnh hưởng tương thích).
        public static OutboxMessage ForInvitation(InvitationEmailJob job) => new()
        {
            Type = InvitationEmailType,
            Payload = JsonSerializer.Serialize(job),
            InvitationId = job.InvitationId,
            CampaignId = job.CampaignId,
            OccurredAt = DateTime.UtcNow
        };
    }
}
