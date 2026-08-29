namespace Isas.CampaignService.Models
{
    /// <summary>
    /// Sàng CV B2B (C13/D18) — 1 dòng / 1 CV ứng viên HR upload. Applicant là người NGOÀI
    /// (chưa có account) → lưu <c>full_name</c>/<c>email</c> parse từ CV, KHÔNG FK sang Auth.
    /// KHÔNG dùng <c>file_records</c> (bảng đó của Interview, gắn <c>user_id</c> ứng viên).
    /// State machine (C13 chỉ tới Filtered/Rejected; Analyzing→Analyzed/AnalysisFailed = C14):
    /// Pending → (parse OK + hard-filter) Filtered | Rejected(reject_reason) | (parse FAIL) Rejected.
    ///
    /// DB16 — bảng đổi tên <c>campaign_candidates</c> → <c>cv_submission</c>; các cột membership
    /// (candidate_id/joined_at/session_id/interview_status/reference_image_key) tách sang
    /// <see cref="CampaignMembership"/>. Đây là "sự thật sàng CV" (screening fact) — quan hệ ứng
    /// viên↔campaign (D2 join) sống độc lập ngay cả khi không có CV (đường 1 mời-thẳng email).
    /// </summary>
    public class CvSubmission
    {
        public Guid Id { get; set; }
        public Guid CampaignId { get; set; }            // FK → campaigns (Cascade); index
        public string? FullName { get; set; }           // parse từ CV; HR sửa qua PATCH (C14)
        public string? Email { get; set; }              // tách từ CV; UNIQUE(campaign_id, email) (null bỏ qua)
        public string? CvFileUrl { get; set; }          // S3 KEY (campaigns/{id}/candidates/{cid}.pdf) — không full URL (GEN-5)
        public string? CvParsedText { get; set; }       // text parse PdfPig — nguồn hard-filter + gửi AI (C14)
        public CvParseStatus ParseStatus { get; set; }  // pending·done·failed
        public CvSubmissionStatus Status { get; set; }  // state machine; index
        public string? RejectReason { get; set; }       // lý do hard-filter loại (vd "thiếu kỹ năng: SQL")

        // ── Kết quả AI (C14 điền — cột định nghĩa sẵn ở C13) ──────────────────
        public List<string>? Skills { get; set; }       // jsonb string[] — AI trả (null tới khi Analyzed)
        public decimal? YearsExperience { get; set; }    // numeric(4,1) — AI trả
        public string? Summary { get; set; }             // AI trả
        // 0–100 — ORDER BY = ranking shortlist. Nay là `jobFitScore` do CvScreeningService TÍNH từ
        // mức bằng chứng, KHÔNG phải số AI phán (xem ScreeningVersion). Giữ nguyên tên cột để
        // sort/keyset/minScore và nhãn FE "Điểm khớp CV" chạy nguyên.
        public int? OverallMatchScore { get; set; }
        public DateTime? LastScreeningPublishedAt { get; set; }   // cho StuckScreeningRepublisher (C15)

        // ── HR technical screener (bước 2-4) ──────────────────────────────────
        // Con dấu thang điểm của OverallMatchScore: null/1 = số cũ do LLM phán trên rubric buổi
        // phỏng vấn, 2 = jobFitScore tính từ bằng chứng. Hai thang KHÔNG so sánh được — có dấu để
        // chúng không bị trộn trong im lặng (tiền lệ scoring_scope_version/BK23).
        public int? ScreeningVersion { get; set; }

        // SCP1 · B5 — GHIM chính sách chấm CV (scoring_policies, kind=CvScreening) mà LẦN ĐÁNH GIÁ này
        // chạy dưới. Ghim TẠI LÚC ĐẨY JOB SÀNG (PublishScreeningJobsAsync), KHÔNG lúc upload.
        //   · Republisher đẩy lại (retry) → GIỮ pin cũ (cùng một lần đánh giá).
        //   · HR bấm rescreen                → PIN LẠI theo campaigns.cv_policy_version hiện hành
        //                                      (lần đánh giá MỚI).
        // Chỉ ghim SỐ VERSION (không ghim biểu thức): Campaign SỞ HỮU bảng scoring_policies và các dòng
        // là BẤT BIẾN (B2) ⇒ (campaign_id, CvScreening, version) resolve về đúng một biểu thức cố định,
        // KHÔNG cần gọi service khác. null = campaign chưa áp chính sách CV / sàng trước cột này.
        public int? ScoringPolicyVersion { get; set; }

        // SCP1 · B7 / HĐ-5 — CỜ LÙI AN TOÀN của OverallMatchScore. true = biểu thức chính sách sàng CV
        // (đã ghim) LỖI lúc chạy (chia 0 / tràn số / ném / kết quả ngoài [0,100]) ⇒ điểm tính bằng
        // công thức CAMP-14 mặc định. Ghi CÙNG transaction với hàng (SaveCvResultAsync) ⇒ NOT NULL
        // default false: hàng sàng trước B7 = false = "không lùi an toàn". Phải hiện ra màn HR (HĐ-5).
        public bool ScoreFallback { get; set; }
        public string? FitSummary { get; set; }                  // 2-3 câu: hợp/không hợp ở đâu
        public List<NeedAssessment>? Strengths { get; set; }      // jsonb — level Strong|Partial
        public List<NeedAssessment>? Gaps { get; set; }           // jsonb — level Weak
        public List<string>? BonusSignals { get; set; }           // jsonb — điểm cộng ngoài JD
        public string? VerificationRisk { get; set; }             // Low|Medium|High — cờ, KHÔNG vào điểm
        public List<string>? VerifyQuestions { get; set; }        // jsonb — tối đa 3, gợi ý cho HR

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
    /// DB16 — <c>Joined</c> KHÔNG còn là trạng thái CV: "đã tham gia" nay biểu diễn bằng SỰ TỒN TẠI của
    /// <see cref="CampaignMembership"/>. <c>Invited</c> GIỮ trên cv_submission (là sự thật sàng CV: đã
    /// phát magic-link từ shortlist).
    /// </summary>
    public enum CvSubmissionStatus
    {
        Pending = 0,
        Filtered = 1,
        Rejected = 2,
        Analyzing = 3,
        Analyzed = 4,
        AnalysisFailed = 5,
        Invited = 6
    }

    /// <summary>D2: tiến độ phỏng vấn của membership (lưu string — GEN-2). null = chưa Start (NotStarted).</summary>
    public enum InterviewProgressStatus
    {
        NotStarted = 0,
        InProgress = 1,
        Abandoned = 2,
        Completed = 3
    }
}
