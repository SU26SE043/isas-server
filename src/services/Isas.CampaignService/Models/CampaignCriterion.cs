namespace Isas.CampaignService.Models
{
    /// <summary>
    /// Tiêu chí chấm CÓ CẤU TRÚC (D9) — sinh khi publish (AI đề xuất / HR sửa).
    /// Σ Weight của 1 campaign = 1. Khi tạo session → gửi sang Interview materialize rubric.
    /// </summary>
    public class CampaignCriterion
    {
        public Guid Id { get; set; }
        public Guid CampaignId { get; set; }
        public int OrderNo { get; set; }           // C12: thứ tự hiển thị (HR sắp); UNIQUE (campaign_id, order_no)
        public string Name { get; set; } = null!;
        public string? Description { get; set; }
        public decimal Weight { get; set; }        // numeric(5,4) — Σ/campaign = 1
        public int MaxScore { get; set; }
        public CriterionSource Source { get; set; }
        public DateTime CreatedAt { get; set; }
        public DateTime UpdatedAt { get; set; }     // C12

        // Navigation
        public Campaign Campaign { get; set; } = null!;

        /// <summary>
        /// CAMP-16/17 — mốc điểm (E9 hard-anchor). Rỗng = chưa khai mốc ⇒ Interview rơi về dải mặc định
        /// như trước tính năng này (hành vi cũ giữ nguyên, không phải trạng thái lỗi).
        /// </summary>
        public ICollection<CampaignCriterionLevel> Levels { get; set; } = new List<CampaignCriterionLevel>();
    }

    public enum CriterionSource
    {
        AiSuggested = 0,
        HrEdited = 1
    }
}
