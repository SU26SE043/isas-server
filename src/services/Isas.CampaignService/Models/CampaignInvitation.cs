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

        // DB23 — SHA-256(token) base64, KHÔNG PHẢI token thô. Token thô chỉ đi trong email/URL gửi
        // ứng viên, không bao giờ nằm trong DB (đọc được DB ≠ mạo danh được invitee). Tra cứu:
        // băm token client gửi lên rồi so với cột này (xem InvitationTokens).
        public string TokenHash { get; set; } = null!;
        public string Email { get; set; } = null!;
        // DB23 — LUÔN có hạn (trước đây nullable → campaign không deadline ⇒ token sống vĩnh viễn).
        // = campaign.ExpiresAt nếu có, else created_at + Invitation:DefaultExpiryDays.
        public DateTime ExpiresAt { get; set; }
        public DateTime? SentAt { get; set; }        // producer-side: đã vào outbox (ghi cùng tx tạo lời mời — DB2b)
        public DateTime? EmailSentAt { get; set; }   // consumer-side: SMTP đã gửi (dedup redeliver — DB2b, khác SentAt)
        public DateTime? UsedAt { get; set; }
        public Guid? SessionId { get; set; }       // ref lỏng → Interview, set khi mở token (D2)
        public DateTime? RevokedAt { get; set; }   // re-issue (D3/D4) vô hiệu token cũ
        public DateTime CreatedAt { get; set; }

        // Navigation
        public Campaign Campaign { get; set; } = null!;

        // Navigation (DB9/DB16) — FK nội-service campaign_invitations.campaign_candidate_id → cv_submission.id.
        // Cột DB giữ tên campaign_candidate_id (không rename cột); nav re-point về CvSubmission (bảng đổi tên).
        // Optional nav (CampaignCandidateId nullable, đường-1 mời-thẳng = null) → OnDelete SetNull:
        // xoá CV chỉ mất link shortlist, invitation giữ lại. KHÔNG cần query filter mới (optional nav).
        public CvSubmission? CvSubmission { get; set; }
    }
}
