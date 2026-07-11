namespace Isas.CampaignService.Models
{
    /// <summary>
    /// Ranking read-model B2B (E4/D10) — cập nhật bằng event <c>SessionScored</c> (RabbitMQ),
    /// KHÔNG gọi HTTP đọc điểm mỗi lần xem dashboard (campaign.md §campaign_rankings).
    /// Idempotent: UNIQUE(session_id) — event tới 2 lần (redelivery/duplicate) vẫn chỉ 1 row (upsert).
    /// <c>Rank</c>/<c>Result</c> (Pass/Fail) do E5 tính khi build tính năng xếp hạng — E4 chỉ ghi
    /// <c>TotalScore</c> (điểm có trọng số Interview đã tính sẵn), để 2 cột đó null.
    /// </summary>
    public class CampaignRanking
    {
        public Guid Id { get; set; }
        public Guid CampaignId { get; set; }
        public Guid CandidateId { get; set; }
        public Guid SessionId { get; set; }   // ref lỏng → Interview; UNIQUE (upsert idempotent)
        public decimal TotalScore { get; set; }
        public int? Rank { get; set; }        // 🔜 E5
        public string? Result { get; set; }   // 🔜 E5: "Pass"/"Fail"
        public DateTime UpdatedAt { get; set; }
    }
}
