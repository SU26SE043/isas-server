namespace Isas.CampaignService.Models
{
    /// <summary>
    /// Sàng CV B2B (C13/D18) — 1 dòng / 1 CV ứng viên HR upload. Applicant là người NGOÀI
    /// (chưa có account) → lưu <c>full_name</c>/<c>email</c> parse từ CV, KHÔNG FK sang Auth.
    /// KHÔNG dùng <c>file_records</c> (bảng đó của Interview, gắn <c>user_id</c> ứng viên).
    /// State machine (C13 chỉ tới Filtered/Rejected; Analyzing→Analyzed/AnalysisFailed = C14):
    /// Pending → (parse OK + hard-filter) Filtered | Rejected(reject_reason) | (parse FAIL) Rejected.
    /// </summary>
    public class CampaignCandidate
    {
        public Guid Id { get; set; }
        public Guid CampaignId { get; set; }            // FK → campaigns (Cascade); index
        public Guid? CandidateId { get; set; }          // ref lỏng → Auth; null tới khi mở magic-link (D2)
        public string? FullName { get; set; }           // parse từ CV; HR sửa qua PATCH (C14)
        public string? Email { get; set; }              // tách từ CV; UNIQUE(campaign_id, email) (null bỏ qua)
        public string? CvFileUrl { get; set; }          // S3 KEY (campaigns/{id}/candidates/{cid}.pdf) — không full URL (GEN-5)
        public string? CvParsedText { get; set; }       // text parse PdfPig — nguồn hard-filter + gửi AI (C14)
        public CvParseStatus ParseStatus { get; set; }  // pending·done·failed
        public CandidateStatus Status { get; set; }     // state machine; index
        public string? RejectReason { get; set; }       // lý do hard-filter loại (vd "thiếu kỹ năng: SQL")

        // ── Kết quả AI (C14 điền — cột định nghĩa sẵn ở C13) ──────────────────
        public List<string>? Skills { get; set; }       // jsonb string[] — AI trả (null tới khi Analyzed)
        public decimal? YearsExperience { get; set; }    // numeric(4,1) — AI trả
        public string? Summary { get; set; }             // AI trả
        public int? OverallMatchScore { get; set; }      // 0–100 — AI trả; ORDER BY = ranking shortlist
        public DateTime? LastScreeningPublishedAt { get; set; }   // cho StuckScreeningRepublisher (C15)

        // ── D2: Membership (giống Discord/Classroom) — set khi ứng viên "Join Campaign" qua magic-link ──
        public DateTime? JoinedAt { get; set; }             // null tới khi ứng viên tham gia (D2)
        public Guid? SessionId { get; set; }                // ref lỏng → Interview; set khi "Start Interview"
        public InterviewProgressStatus? InterviewStatus { get; set; }   // NotStarted/InProgress/Completed (enum string)

        public DateTime CreatedAt { get; set; }
        public DateTime UpdatedAt { get; set; }

        // Navigation
        public Campaign Campaign { get; set; } = null!;
        public ICollection<CandidateCriterionScore> CriterionScores { get; set; } = new List<CandidateCriterionScore>();
    }

    /// <summary>Trạng thái parse PDF của 1 CV (lưu string — GEN-2).</summary>
    public enum CvParseStatus
    {
        Pending = 0,
        Done = 1,
        Failed = 2
    }

    /// <summary>
    /// State machine ứng viên sàng CV (lưu string — GEN-2). C13 dùng: Pending/Filtered/Rejected.
    /// Analyzing/Analyzed/AnalysisFailed/Invited = C14/C15 (định nghĩa sẵn để state machine đủ).
    /// Joined = D2 (ứng viên đã tham gia campaign qua magic-link — có account Candidate + membership).
    /// </summary>
    public enum CandidateStatus
    {
        Pending = 0,
        Filtered = 1,
        Rejected = 2,
        Analyzing = 3,
        Analyzed = 4,
        AnalysisFailed = 5,
        Invited = 6,
        Joined = 7
    }

    /// <summary>D2: tiến độ phỏng vấn của membership (lưu string — GEN-2). null = chưa Start (NotStarted).</summary>
    public enum InterviewProgressStatus
    {
        NotStarted = 0,
        InProgress = 1,
        Completed = 2
    }
}
