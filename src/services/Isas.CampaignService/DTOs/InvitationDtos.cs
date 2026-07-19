namespace Isas.CampaignService.DTOs
{
    /// <summary>D1 — Distribution đường 1: mời thẳng qua danh sách email.</summary>
    public class CreateInvitationsRequest
    {
        public List<string> Emails { get; set; } = new();
    }

    public class InvitationItem
    {
        public Guid Id { get; set; }
        public string Email { get; set; } = null!;
        public DateTime? ExpiresAt { get; set; }
    }

    /// <summary>Email hỏng/trùng/đã mời → nằm ở đây, KHÔNG chặn cả batch.</summary>
    public class FailedInvitationItem
    {
        public string Email { get; set; } = null!;
        public string Reason { get; set; } = null!;
    }

    public class CreateInvitationsResponse
    {
        public List<InvitationItem> Created { get; set; } = new();
        public List<FailedInvitationItem> Failed { get; set; } = new();
    }

    /// <summary>
    /// 1 dòng trong danh sách lời mời đã phát của campaign (HR theo dõi "đã mời ai / mail gửi tới đâu").
    /// Trước đây <c>created[]</c> chỉ trả tại thời điểm POST → refresh trang là mất dấu; đường-1 (mời thẳng
    /// email) lại KHÔNG sinh row <c>cv_submission</c> nên <c>GET /candidates</c> cũng không thấy.
    ///
    /// DB23 — KHÔNG bao giờ trả token (DB chỉ giữ hash, và join = JWT candidate chứ không phải HR cầm token).
    /// </summary>
    public class InvitationListItem
    {
        public Guid Id { get; set; }
        public string Email { get; set; } = null!;

        /// <summary>Suy ra read-time, xem <see cref="InvitationDeliveryStatus"/>.</summary>
        public string Status { get; set; } = null!;

        public DateTime? SentAt { get; set; }        // DB2b producer-side: đã vào outbox
        public DateTime? EmailSentAt { get; set; }   // DB2b consumer-side: SMTP đã gửi thật
        public DateTime ExpiresAt { get; set; }
        public DateTime? RevokedAt { get; set; }
        public DateTime? JoinedAt { get; set; }      // từ membership (D2), null = chưa tham gia
        public Guid? CampaignCandidateId { get; set; }   // đường-2: link về cv_submission đã sàng; đường-1 = null
        public DateTime CreatedAt { get; set; }
    }

    /// <summary>
    /// Trạng thái giao lời mời — suy ra read-time từ các mốc thời gian, KHÔNG lưu cột riêng
    /// (tránh thêm state phải đồng bộ). Thứ tự ưu tiên khi suy: Revoked → Joined → Expired → Sent → Queued.
    /// Revoked đứng TRƯỚC Joined có chủ ý: sau reissue (D4), lời mời cũ phải hiện Revoked chứ không
    /// "thơm lây" trạng thái Joined của lời mời mới cùng email.
    /// </summary>
    public static class InvitationDeliveryStatus
    {
        public const string Revoked = "Revoked";   // D4 reissue vô hiệu token cũ
        public const string Joined = "Joined";     // đã có membership (D2)
        public const string Expired = "Expired";   // quá ExpiresAt mà chưa join
        public const string Sent = "Sent";         // consumer đã gửi SMTP (email_sent_at)
        public const string Queued = "Queued";     // mới vào outbox, dispatcher/consumer chưa gửi xong
    }
}
