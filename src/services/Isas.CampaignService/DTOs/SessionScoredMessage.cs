namespace Isas.CampaignService.DTOs
{
    /// <summary>
    /// Shape của event <c>SessionScored</c> nhận từ InterviewService qua RabbitMQ
    /// (exchange <c>interview.events</c> topic, routing key <c>session.scored</c> —
    /// interview.md §Sự kiện phát ra). Bản sao CỤC BỘ trong CampaignService — KHÔNG
    /// tham chiếu thẳng code/DLL InterviewService (GEN-2: không FK/dependency xuyên
    /// service, ref lỏng bằng Guid). Field khớp
    /// <c>Isas.InterviewService.DTOs.SessionScoredEvent</c>.
    /// </summary>
    public class SessionScoredMessage
    {
        public Guid SessionId { get; set; }

        // null = B2C → E4 chỉ xếp hạng B2B, bỏ qua (không tạo row campaign_rankings).
        public Guid? CampaignId { get; set; }

        public Guid CandidateId { get; set; }

        // Điểm tổng ĐÃ có trọng số (Σ điểm_tiêu_chí × weight, kẹp [0,100]) — Interview tính sẵn,
        // Campaign lưu nguyên, KHÔNG recompute.
        public decimal TotalScore { get; set; }

        public DateTime ScoredAt { get; set; }

        // CAMP-18 — bản thước đo Interview đã dùng để chấm buổi này. NULLABLE có chủ đích: bản
        // Interview cũ không gửi field này, và hai service deploy không nguyên tử ⇒ thiếu thì để
        // NULL ("không biết"), tuyệt đối không mặc định thành 1.
        public int? RubricVersion { get; set; }
    }
}
