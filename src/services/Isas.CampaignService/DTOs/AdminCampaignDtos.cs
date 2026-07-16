using Isas.CampaignService.Models;

namespace Isas.CampaignService.DTOs
{
    /// <summary>
    /// PlatformAdmin oversight (AUTH-7) — GET /campaign/admin/campaigns. Một dòng tóm tắt campaign
    /// XUYÊN org (không lọc theo org của caller). Read-only; tôn trọng soft-delete (D11 — global query
    /// filter DeletedAt==null). Khác CampaignResponse: gọn (không kèm questions/criteria).
    /// </summary>
    public class AdminCampaignListItem
    {
        public Guid Id { get; set; }
        public Guid OrgId { get; set; }
        public string Title { get; set; } = null!;
        public string? Domain { get; set; }
        public string Status { get; set; } = null!;
        public int? MaxCandidates { get; set; }
        public DateTime? StartsAt { get; set; }
        public DateTime? ExpiresAt { get; set; }
        public DateTime CreatedAt { get; set; }

        public static AdminCampaignListItem FromEntity(Campaign c) => new()
        {
            Id = c.Id,
            OrgId = c.OrgId,
            Title = c.Title,
            Domain = c.Domain,
            Status = c.Status.ToString(),
            MaxCandidates = c.MaxCandidates,
            StartsAt = c.StartsAt,
            ExpiresAt = c.ExpiresAt,
            CreatedAt = c.CreatedAt
        };
    }
}
