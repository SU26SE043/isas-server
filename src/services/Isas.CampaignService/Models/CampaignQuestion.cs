namespace Isas.CampaignService.Models
{
    public class CampaignQuestion
    {
        public Guid Id { get; set; }
        public Guid CampaignId { get; set; }
        public Guid OrgId { get; set; }   // BK4: owner denormalize theo campaign = ORG (AUTH-8)
        public string QuestionText { get; set; }
        public QuestionSource Source { get; set; }
        public int? TimeLimitSeconds { get; set; }
        public bool IsRequired { get; set; }
        public DateTime CreatedAt { get; set; }

        // Navigation
        public Campaign Campaign { get; set; } = null!;
    }

    public enum QuestionSource
    {
        AiGenerated = 0,
        CustomHr = 1
    }
}