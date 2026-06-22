using Isas.CampaignService.Models;
using System.ComponentModel.DataAnnotations;

namespace Isas.CampaignService.DTOs
{
    public class QuestionItem
    {
        public string QuestionText { get; set; }
        public QuestionSource Source { get; set; }
        public bool IsRequired { get; set; } = true;
    }

    public class CreateCampaignRequest
    {
        [Required]
        public string Title { get; set; }

        [Required]
        public string? Domain { get; set; }

        public int? MaxCandidates { get; set; }

        [Required]
        public int? TimeLimitMinutes { get; set; }

        public bool AntiCheatEnabled { get; set; }

        [Required]
        public DateTime? StartsAt { get; set; }

        [Required]
        public DateTime? ExpiresAt { get; set; }

        public List<QuestionItem> Questions { get; set; } = new();
    }

    public class UploadCampaignFilesRequest
    {
        public IFormFile? JdFile { get; set; }
        public IFormFile? CriteriaFile { get; set; }
    }

    public class UpdateCampaignRequest
    {
        public string Title { get; set; }

        public string? Domain { get; set; }

        public int? MaxCandidates { get; set; }

        public int? TimeLimitMinutes { get; set; }

        public bool AntiCheatEnabled { get; set; }

        public DateTime? StartsAt { get; set; }

        public DateTime? ExpiresAt { get; set; }
    }

    public class CampaignQuestionResponse
    {
        public Guid Id { get; set; }
        public string QuestionText { get; set; }
        public string Source { get; set; }
        public bool IsRequired { get; set; }
    }

    public class CampaignResponse
    {
        public Guid Id { get; set; }
        public Guid EmployerId { get; set; }
        public string Title { get; set; }
        public string? Domain { get; set; }
        public string Status { get; set; }
        public int? MaxCandidates { get; set; }
        public int? TimeLimitMinutes { get; set; }
        public bool AntiCheatEnabled { get; set; }
        public DateTime? StartsAt { get; set; }
        public DateTime? ExpiresAt { get; set; }
        public List<CampaignQuestionResponse> Questions { get; set; }
        public string? JDText { get; set; }
        public string? CriteriaText { get; set; }
        public DateTime CreatedAt { get; set; }
        public DateTime UpdatedAt { get; set; }

        public static CampaignResponse FromEntity(Campaign c) => new CampaignResponse
        {
            Id = c.Id,
            EmployerId = c.EmployerId,
            Title = c.Title,
            Domain = c.Domain,
            Status = c.Status.ToString(),
            MaxCandidates = c.MaxCandidates,
            TimeLimitMinutes = c.TimeLimitMinutes,
            AntiCheatEnabled = c.AntiCheatEnabled,
            StartsAt = c.StartsAt,
            ExpiresAt = c.ExpiresAt,
            Questions = c.Questions.Select(q => new CampaignQuestionResponse
            {
                Id = q.Id,
                QuestionText = q.QuestionText,
                Source = q.Source.ToString(),
                IsRequired = q.IsRequired
            }).ToList(),
            JDText = c.JDText,
            CriteriaText = c.CriteriaText,
            CreatedAt = c.CreatedAt,
            UpdatedAt = c.UpdatedAt
        };
    }
}
