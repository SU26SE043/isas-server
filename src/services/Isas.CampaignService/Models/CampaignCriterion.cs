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
        public string Name { get; set; } = null!;
        public string? Description { get; set; }
        public decimal Weight { get; set; }        // numeric(5,4) — Σ/campaign = 1
        public int MaxScore { get; set; }
        public CriterionSource Source { get; set; }
        public DateTime CreatedAt { get; set; }

        // Navigation
        public Campaign Campaign { get; set; } = null!;
    }

    public enum CriterionSource
    {
        AiSuggested = 0,
        HrEdited = 1
    }
}
