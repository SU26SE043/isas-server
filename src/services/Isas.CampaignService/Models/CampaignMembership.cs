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

        // FX1 — FK → campaign_invitations (SetNull): LỜI MỜI đã dẫn tới lần join này. DB16 tách bảng God
        // nhưng KHÔNG dựng lại quan hệ này, nên "membership ↔ invitation" trước đây phải suy bằng cách
        // GHÉP EMAIL (GetInvitationsAsync) — suy đoán, sai khi cùng email được mời nhiều lần.
        // Biết được TẠI THỜI ĐIỂM GHI: join luôn đi từ token của một invitation cụ thể.
        // NULLABLE: (a) membership lịch sử (tạo trước FX1) chỉ backfill được khi CHẮC CHẮN — xem
        // comment trong migration; (b) SetNull khi lời mời bị xoá cứng.
        // Nhánh idempotent GHI ĐÈ bằng lời mời MỚI NHẤT dùng để join (không phải `??=`): sau reissue
        // (D4) lời mời cũ đã Revoked nên không join lại được, giá trị mới luôn là lời mời còn hiệu lực.
        public Guid? InvitationId { get; set; }
        public Guid? CandidateId { get; set; }              // ref lỏng → Auth; UNIQUE(campaign_id, candidate_id)
        public MembershipStatus Status { get; set; }        // enum string; default Joined (D2)
        public DateTime? JoinedAt { get; set; }             // null tới khi ứng viên tham gia (D2)
        public Guid? SessionId { get; set; }                // ref lỏng → Interview; set khi "Start Interview"
        public Guid? SlotId { get; set; }
        public DateTime? InterviewDeadlineAt { get; set; }
        public InterviewProgressStatus? InterviewStatus { get; set; }   // NotStarted/InProgress/Completed (enum string)
        // MON1-B1 — mốc SERVER ghi lần ĐẦU TIÊN membership chuyển sang InProgress (ParticipationService).
        // Set 1 lần (`??=` trong khối chuyển trạng thái): resume KHÔNG dời, và LÀM LẠI lượt 2 (ATT1) cũng
        // KHÔNG dời — nghĩa là "ứng viên bắt đầu phỏng vấn lần đầu lúc nào" (mốc phễu của analytics). Mốc
        // của lượt ĐANG giữ nằm ở AttemptStartedAt. null = "chưa bắt đầu" HOẶC "không biết" (có trước migration).
        public DateTime? InterviewStartedAt { get; set; }
        // Mốc SERVER ghi lúc Start tạo ra buổi MỚI (lượt hiện tại — buổi ở SessionId). Ghi trong khối
        // `isNewSession` cùng AttemptCount++ và xoá AbandonReason: một vị ngữ "buổi mới" cho mọi field
        // theo lượt. Vào lại buổi đang dở KHÔNG dời. Điểm neo của MonitoringGapSweeper LUẬT 2 (thời lượng
        // buổi = mốc kết thúc − mốc này) và giá trị `interviewStartedAt` của dòng unscoredFlagged (AC2).
        // null = "không biết" (BK23): dòng có trước cột chỉ được điền khi chắc chỉ có một lượt.
        public DateTime? AttemptStartedAt { get; set; }
        // ATT1 — số LƯỢT làm bài đã dùng. Một lượt = một lần Start tạo ra buổi thi MỚI (Interview trả về
        // session KHÁC session membership đang giữ). Vào lại buổi đang dở / gọi trùng KHÔNG tính; Start
        // ném trước khi có buổi (402/429/502/lỗi sinh câu hỏi) KHÔNG tính. So với campaigns.max_attempts.
        public int AttemptCount { get; set; }
        // AC2 — lý do Interview báo buổi BỎ NGANG (`SessionAbandoned.Reason`: no_scored_answer ·
        // expired_no_answer · generation_failed …), ghi bởi RankingEventHandler cho buổi ĐANG giữ ở
        // SessionId; Start tạo lượt mới (ATT1) thì xoá cùng lúc gán SessionId mới. Text tự do, KHÔNG CHECK,
        // KHÔNG HasMaxLength (tiền lệ reject_reason/transcript_engine: Interview thêm lý do mới thì
        // Campaign không được nổ). null = "không biết" (BK23) — KHÔNG backfill dòng có trước cột này.
        public string? AbandonReason { get; set; }
        // SEC-2/DATA-2: ảnh tham chiếu face-verify — 1 bản/ứng viên/campaign. Lưu S3 KEY (không ảnh trong DB), null tới khi có.
        public string? ReferenceImageKey { get; set; }

        // F5 — danh tính người-đọc-được cho HR (bảng kết quả + CSV export). Trước F5 mọi cột export đều là
        // UUID nên file tải về gần như vô dụng. Snapshot tại thời điểm join (đường-1 lấy từ invitation.Email,
        // đường-2 từ cv_submission) — KHÔNG gọi Auth lúc đọc (GEN-3: service không call Auth lúc chạy).
        // NULLABLE có chủ ý: (a) NOT NULL + default sẽ rewrite bảng lúc apply; (b) membership đường-1 lịch sử
        // (tạo trước F5) không có nguồn dữ liệu nào để backfill — xem comment trong migration.
        //
        // FX1 — GIỮ LẠI CÓ CHỦ ĐÍCH dù nay đã có <see cref="InvitationId"/> đọc được cùng dữ liệu:
        // membership đường-1 join SAU F5 nhưng TRƯỚC FX1 có email ở đây mà KHÔNG backfill được
        // `invitation_id` (không khoá nối nào chắc chắn — xem migration). Bỏ cột = XOÁ VĨNH VIỄN email
        // của đúng nhóm đó, không đường nào dựng lại. Vậy nên đây là snapshot chủ đích, không phải cột
        // trùng bỏ quên; `invitation_id` là QUAN HỆ (dùng để ghép chính xác), 2 cột này là DỮ LIỆU.
        public string? FullName { get; set; }
        public string? Email { get; set; }

        public DateTime CreatedAt { get; set; }
        public DateTime UpdatedAt { get; set; }

        // Navigation
        public Campaign Campaign { get; set; } = null!;
        // Optional nav (CvSubmissionId nullable) — KHÔNG cần query filter (D2 đường-1 không có CV).
        public CvSubmission? CvSubmission { get; set; }
        // FX1 — optional nav (InvitationId nullable) → KHÔNG cần query filter mới.
        public CampaignInvitation? Invitation { get; set; }
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
