namespace Isas.CampaignService.Models
{
    /// <summary>
    /// Ranking read-model B2B (E4/D10) — cập nhật bằng event <c>SessionScored</c> (RabbitMQ),
    /// KHÔNG gọi HTTP đọc điểm mỗi lần xem dashboard (campaign.md §campaign_rankings).
    /// Idempotent: UNIQUE(session_id) — event tới 2 lần (redelivery/duplicate) vẫn chỉ 1 row (upsert).
    /// Rank + Pass/Fail do E5 (<c>GetCampaignResultsAsync</c>) tính READ-TIME từ <c>TotalScore</c> —
    /// KHÔNG lưu thành cột (BK1: đã drop cột chết <c>rank</c>/<c>result</c> mà E4 từng tạo nhưng E5 không đọc).
    /// E4 chỉ ghi <c>TotalScore</c> (điểm có trọng số Interview đã tính sẵn).
    /// </summary>
    public class CampaignRanking
    {
        public Guid Id { get; set; }
        public Guid CampaignId { get; set; }
        public Guid CandidateId { get; set; }
        public Guid SessionId { get; set; }   // ref lỏng → Interview; UNIQUE (upsert idempotent)
        public decimal TotalScore { get; set; }
        public DateTime UpdatedAt { get; set; }
    }
}
