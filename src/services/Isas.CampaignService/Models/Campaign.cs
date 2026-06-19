namespace Isas.CampaignService.Models
{
    public class Campaign
    {
        public Guid Id { get; set; }
        public Guid EmployerId { get; set; }
        public string Title { get; set; }
        public string? Domain { get; set; }
        public CampaignStatus Status { get; set; }
        public int? MaxCandidates { get; set; }
        public int? TimeLimitMinutes { get; set; }
        public bool AntiCheatEnabled { get; set; }
        public string? JDFileUrl { get; set; }
        public string? JDText { get; set; }
        public string? CriteriaFileUrl { get; set; }
        public string? CriteriaText { get; set; }
        public DateTime? StartsAt { get; set; }
        public DateTime? ExpiresAt { get; set; }
        public DateTime CreatedAt { get; set; }
        public DateTime UpdatedAt { get; set; }

        // Navigation
        public ICollection<CampaignQuestion> Questions { get; set; } = new List<CampaignQuestion>();
    }

    public enum CampaignStatus
    {
        Draft,
        Active,
        Closed,
        Archived
    }
}