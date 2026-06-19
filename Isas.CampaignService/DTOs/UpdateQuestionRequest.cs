using Isas.CampaignService.Models;
using System.ComponentModel.DataAnnotations;

namespace Isas.CampaignService.DTOs
{
    public class UpdateQuestionRequest
    {
        public string QuestionText { get; set; }

        public QuestionSource Source { get; set; }

        [Range(60, int.MaxValue, ErrorMessage = "The limit must be at least 60 seconds.")]
        public int? TimeLimitSeconds { get; set; }
    }
}
