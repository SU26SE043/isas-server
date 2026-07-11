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
        // E5: ngưỡng % điểm tổng để auto pass/fail (0–100, CAMP-11). null = không auto → HR quyết tay.
        public int? PassScorePct { get; set; }
        public string? JDFileUrl { get; set; }
        public string? JDText { get; set; }
        public string? CriteriaFileUrl { get; set; }
        public string? CriteriaText { get; set; }
        // C13: rule cứng sàng CV (hard-filter, set khi Draft). null = không áp rule đó.
        public List<string>? RequiredSkills { get; set; }   // jsonb — phải có ĐỦ trong cv_parsed_text
        public List<string>? KeywordsAny { get; set; }      // jsonb — có ≥1 từ khóa
        public int? MinYearsExperience { get; set; }        // số năm KN tối thiểu
        public DateTime? StartsAt { get; set; }
        public DateTime? ExpiresAt { get; set; }
        public DateTime CreatedAt { get; set; }
        public DateTime UpdatedAt { get; set; }
        public DateTime? DeletedAt { get; set; }   // soft delete (D11): null = còn sống

        // Navigation
        public ICollection<CampaignQuestion> Questions { get; set; } = new List<CampaignQuestion>();
        public ICollection<CampaignCriterion> Criteria { get; set; } = new List<CampaignCriterion>();
        public ICollection<CampaignInvitation> Invitations { get; set; } = new List<CampaignInvitation>();
        public ICollection<CampaignCandidate> Candidates { get; set; } = new List<CampaignCandidate>();   // C13: sàng CV
    }

    public enum CampaignStatus
    {
        Draft,
        Active,
        Closed,
        Archived
    }
}