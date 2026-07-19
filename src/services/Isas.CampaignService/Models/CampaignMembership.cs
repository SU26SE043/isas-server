namespace Isas.CampaignService.Models
{
    /// <summary>
    /// D2 — Membership (giống Discord/Classroom): quan hệ ỨNG VIÊN ↔ CAMPAIGN, tạo khi ứng viên
    /// "Join Campaign" qua magic-link. Sống ĐỘC LẬP với <see cref="CvSubmission"/> (đường-1 mời-thẳng
    /// email KHÔNG có CV → <c>CvSubmissionId</c> = null; đường-2 shortlist → trỏ về row CV đã sàng).
    ///
    /// DB16 — tách khỏi bảng God <c>campaign_candidates</c> cũ (nay là <c>cv_submission</c>). "Đã tham
    /// gia" (trước là <c>CandidateStatus.Joined</c>) nay = SỰ TỒN TẠI của row membership này.
    /// </summary>
    public class CampaignMembership
    {
        public Guid Id { get; set; }
        public Guid CampaignId { get; set; }                // FK → campaigns (Cascade); index
        // FK → cv_submission (SetNull) — đường-2 shortlist trỏ về CV đã sàng; null = đường-1 (mời-thẳng email).
        public Guid? CvSubmissionId { get; set; }
        public Guid? CandidateId { get; set; }              // ref lỏng → Auth; UNIQUE(campaign_id, candidate_id)
        public MembershipStatus Status { get; set; }        // enum string; default Joined (D2)
        public DateTime? JoinedAt { get; set; }             // null tới khi ứng viên tham gia (D2)
        public Guid? SessionId { get; set; }                // ref lỏng → Interview; set khi "Start Interview"
        public InterviewProgressStatus? InterviewStatus { get; set; }   // NotStarted/InProgress/Completed (enum string)
        // SEC-2/DATA-2: ảnh tham chiếu face-verify — 1 bản/ứng viên/campaign. Lưu S3 KEY (không ảnh trong DB), null tới khi có.
        public string? ReferenceImageKey { get; set; }

        // F5 — danh tính người-đọc-được cho HR (bảng kết quả + CSV export). Trước F5 mọi cột export đều là
        // UUID nên file tải về gần như vô dụng. Snapshot tại thời điểm join (đường-1 lấy từ invitation.Email,
        // đường-2 từ cv_submission) — KHÔNG gọi Auth lúc đọc (GEN-3: service không call Auth lúc chạy).
        // NULLABLE có chủ ý: (a) NOT NULL + default sẽ rewrite bảng lúc apply; (b) membership đường-1 lịch sử
        // (tạo trước F5) không có nguồn dữ liệu nào để backfill — xem comment trong migration.
        public string? FullName { get; set; }
        public string? Email { get; set; }

        public DateTime CreatedAt { get; set; }
        public DateTime UpdatedAt { get; set; }

        // Navigation
        public Campaign Campaign { get; set; } = null!;
        // Optional nav (CvSubmissionId nullable) — KHÔNG cần query filter (D2 đường-1 không có CV).
        public CvSubmission? CvSubmission { get; set; }
    }

    /// <summary>
    /// Trạng thái membership (lưu string — GEN-2). DB16 — hiện chỉ <c>Joined</c>: "đã tham gia" biểu
    /// diễn bằng SỰ TỒN TẠI row membership (thay <c>CandidateStatus.Joined</c> cũ trên bảng God). Để mở
    /// rộng về sau (vd Left/Removed) nên vẫn là enum string.
    /// </summary>
    public enum MembershipStatus
    {
        Joined = 0
    }
}
