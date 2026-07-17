namespace Isas.CampaignService.Models
{
    /// <summary>
    /// Magic-link mời ứng viên (D1 — Distribution đường 1: mời thẳng qua danh sách email).
    /// Token dùng 1 lần NỘP (resume tới submit, khóa sau submit — D2, chưa build ở đây).
    /// </summary>
    public class CampaignInvitation
    {
        public Guid Id { get; set; }
        public Guid CampaignId { get; set; }

        // 🔜 C15 (đường 2 — từ shortlist sàng CV): ref lỏng tới campaign_candidates.
        // Bảng campaign_candidates CHƯA build (C13) nên KHÔNG có FK thật ở đây — luôn null cho đường 1 (D1).
        public Guid? CampaignCandidateId { get; set; }

        public string Token { get; set; } = null!;
        public string Email { get; set; } = null!;
        public DateTime? ExpiresAt { get; set; }   // <= campaign.ExpiresAt; null nếu campaign không có hạn
        public DateTime? SentAt { get; set; }        // producer-side: đã vào outbox (ghi cùng tx tạo lời mời — DB2b)
        public DateTime? EmailSentAt { get; set; }   // consumer-side: SMTP đã gửi (dedup redeliver — DB2b, khác SentAt)
        public DateTime? UsedAt { get; set; }
        public Guid? SessionId { get; set; }       // ref lỏng → Interview, set khi mở token (D2)
        public DateTime? RevokedAt { get; set; }   // re-issue (D3/D4) vô hiệu token cũ
        public DateTime CreatedAt { get; set; }

        // Navigation
        public Campaign Campaign { get; set; } = null!;
    }
}
