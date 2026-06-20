using Isas.CampaignService.Models;
using Microsoft.AspNetCore.Mvc.ModelBinding;
using System.ComponentModel.DataAnnotations;

namespace Isas.CampaignService.DTOs
{
    public class QuestionItem
    {
        public string QuestionText { get; set; }
        public QuestionSource Source { get; set; }
        public int? TimeLimitSeconds { get; set; }
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

        public string? QuestionsJson { get; set; }

        [BindNever]
        public List<QuestionItem> Questions { get; set; } = new();

        public IFormFile? JdFile { get; set; }

        public IFormFile? CriteriaFile { get; set; }
    }

    public class UpdateCampaignRequest
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

        public string? QuestionsJson { get; set; }

        public List<QuestionItem> Questions { get; set; } = new();

        public IFormFile? JdFile { get; set; }

        public IFormFile? CriteriaFile { get; set; }
    }

    public class CampaignQuestionResponse
    {
        public Guid Id { get; set; }
        public string QuestionText { get; set; }
        public string Source { get; set; }
        public int? TimeLimitSeconds { get; set; }
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
        public string? JDFileUrl { get; set; }
        public string? CriteriaFileUrl { get; set; }
        public List<CampaignQuestionResponse> Questions { get; set; } = new();
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
            JDFileUrl = c.JDFileUrl,
            CriteriaFileUrl = c.CriteriaFileUrl,
            Questions = c.Questions.Select(q => new CampaignQuestionResponse
            {
                Id = q.Id,
                QuestionText = q.QuestionText,
                Source = q.Source.ToString(),
                TimeLimitSeconds = q.TimeLimitSeconds,
                IsRequired = q.IsRequired
            }).ToList(),
            CreatedAt = c.CreatedAt,
            UpdatedAt = c.UpdatedAt
        };
    }
}
