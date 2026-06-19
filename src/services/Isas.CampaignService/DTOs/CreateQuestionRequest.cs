using Isas.CampaignService.Models;
using System.ComponentModel.DataAnnotations;

namespace Isas.CampaignService.DTOs
{
    public class CreateQuestionRequest
    {
        [Required]
        public Guid CampaignId { get; set; }

        [Required]
        public string QuestionText { get; set; }

        [Required]
        public QuestionSource Source { get; set; }

        [Required]
        [Range(60, int.MaxValue, ErrorMessage = "The limit must be at least 60 seconds.")]
        public int? TimeLimitSeconds { get; set; }
    }
}
