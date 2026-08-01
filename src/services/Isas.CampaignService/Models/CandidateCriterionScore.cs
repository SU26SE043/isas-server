namespace Isas.CampaignService.Models
{
    /// <summary>
    /// Điểm khớp CV theo TỪNG tiêu chí (C14 — mẫu <c>answer_scores</c>). Bảng tạo ở C13 nhưng
    /// AI mới điền khi callback <c>cv-result</c> (C14). TÁI DÙNG <c>campaign_criteria</c> làm rubric.
    /// </summary>
    public class CandidateCriterionScore
    {
        public Guid Id { get; set; }
        public Guid CvSubmissionId { get; set; } // FK → cv_submission (Cascade); UNIQUE(cv_submission_id, criterion_id)
        public Guid CriterionId { get; set; }   // FK → campaign_criteria (Restrict) — chặn id AI bịa
        public decimal MatchScore { get; set; } // numeric(5,2) — kẹp [0, max_score]
        public string? Reasoning { get; set; }  // dẫn chứng từ CV
        public DateTime CreatedAt { get; set; }

        // Navigation to the screened CV submission.
        public CvSubmission CvSubmission { get; set; } = null!;
        public CampaignCriterion Criterion { get; set; } = null!;
    }
}
