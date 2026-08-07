namespace Isas.CampaignService.DTOs
{
    // ── C14 — Callback worker sàng CV → CampaignService (X-Internal-Token) ──────────────
    // Shape khớp ai.md §Pipeline sàng CV: worker gọi cùng `analyze_cv`, callback về Campaign.
    // candidateId lấy từ ROUTE (không nằm trong body).

    /// <summary>cv-result — kết quả AI chấm khớp 1 CV theo tiêu chí campaign.</summary>
    public class CvResultCallbackRequest
    {
        /// <summary>BK28 — họ tên rút từ CV. <c>null</c> = CV không có tên rõ ràng (hợp lệ) ⇒
        /// <see cref="Services.CvScreeningService.SaveCvResultAsync"/> giữ nguyên giá trị đang có,
        /// KHÔNG ghi đè tên HR đã nhập tay qua PATCH.</summary>
        public string? FullName { get; set; }
        public List<string>? Skills { get; set; }
        public decimal? YearsExperience { get; set; }
        public List<string>? Education { get; set; }   // chấp nhận nhưng KHÔNG lưu (C13 schema không có cột)
        public string? Summary { get; set; }
        public int OverallMatchScore { get; set; }     // 0–100 (kẹp lại phía Campaign phòng AI vượt biên)
        public List<CriterionMatchItem> CriterionMatches { get; set; } = new();
    }

    /// <summary>Điểm khớp CV theo 1 tiêu chí — TÁI DÙNG rubric <c>campaign_criteria</c>.</summary>
    public class CriterionMatchItem
    {
        public Guid CriterionId { get; set; }   // phải khớp campaign_criteria.id (id AI bịa → bỏ)
        public decimal MatchScore { get; set; } // kẹp [0, max_score] phía Campaign
        public string? Reasoning { get; set; }
    }

    /// <summary>cv-failed — worker báo lỗi vĩnh viễn khi phân tích 1 CV.</summary>
    public class CvFailedCallbackRequest
    {
        public string? Reason { get; set; }
    }

    // ── C14 — Shortlist (đọc kết quả sàng cho HR) ──────────────────────────────────────

    /// <summary>1 dòng shortlist (GET danh sách). <c>OverallMatchScore</c> null tới khi Analyzed.</summary>
    public class CandidateListItem
    {
        public Guid Id { get; set; }
        public string? FullName { get; set; }
        public string? Email { get; set; }
        public string Status { get; set; } = null!;
        public int? OverallMatchScore { get; set; }
        public List<string>? Skills { get; set; }
    }

    /// <summary>Chi tiết 1 ứng viên (GET đơn) — kèm điểm + reasoning từng tiêu chí + KEY CV gốc.</summary>
    public class CandidateDetailResponse
    {
        public Guid Id { get; set; }
        public string? FullName { get; set; }
        public string? Email { get; set; }
        public string Status { get; set; } = null!;
        public int? OverallMatchScore { get; set; }
        public List<string>? Skills { get; set; }
        public decimal? YearsExperience { get; set; }
        public string? Summary { get; set; }
        public string? RejectReason { get; set; }   // lý do Rejected (hard-filter) hoặc AnalysisFailed (AI lỗi)
        public string? CvFileUrl { get; set; }       // S3 KEY (GEN-5 — không full URL)
        public List<CriterionScoreItem> CriterionScores { get; set; } = new();
    }

    public class CriterionScoreItem
    {
        public Guid CriterionId { get; set; }
        public string CriterionName { get; set; } = null!;
        public decimal MatchScore { get; set; }
        public int MaxScore { get; set; }
        public string? Reasoning { get; set; }
    }

    /// <summary>PATCH — HR bổ sung/sửa email/fullName khi CV không tách được (chỉ trường gửi lên).</summary>
    public class PatchCandidateRequest
    {
        public string? Email { get; set; }
        public string? FullName { get; set; }
    }
}
