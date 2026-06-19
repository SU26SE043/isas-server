using Isas.CampaignService.Models;

namespace Isas.CampaignService.DTOs
{
    public record QuestionItem(
        string QuestionText,
        QuestionSource Source,
        int? TimeLimitSeconds,
        bool IsRequired = true
    );

    public record CreateCampaignRequest(
        string Title,
        string? JobDescription,
        string? Domain,
        int CreditCost,
        int? MaxCandidates,
        int? TimeLimitMinutes,
        bool AntiCheatEnabled,
        DateTime? StartsAt,
        DateTime? ExpiresAt,
        IFormFile? JdFile,
        IFormFile? CriteriaFile,
        List<QuestionItem> Questions
    );

    public record UpdateCampaignRequest(
        string Title,
        string? JobDescription,
        string? Domain,
        int CreditCost,
        int? MaxCandidates,
        int? TimeLimitMinutes,
        bool AntiCheatEnabled,
        DateTime? StartsAt,
        DateTime? ExpiresAt,
        IFormFile? JdFile,
        IFormFile? CriteriaFile,
        List<QuestionItem> Questions
    );

    public record CampaignQuestionResponse(
        Guid Id,
        string QuestionText,
        string Source,
        int? TimeLimitSeconds,
        bool IsRequired
    );

    public record CampaignResponse(
        Guid Id,
        Guid EmployerId,
        string Title,
        string? Domain,
        string Status,
        int? MaxCandidates,
        int? TimeLimitMinutes,
        bool AntiCheatEnabled,
        DateTime? StartsAt,
        DateTime? ExpiresAt,
        string? JDFileUrl,
        string? CriteriaFileUrl,
        List<CampaignQuestionResponse> Questions,
        DateTime CreatedAt,
        DateTime UpdatedAt
    )
    {
        public static CampaignResponse FromEntity(Campaign c) => new(
            c.Id,
            c.EmployerId,
            c.Title,
            c.Domain,
            c.Status.ToString(),
            c.MaxCandidates,
            c.TimeLimitMinutes,
            c.AntiCheatEnabled,
            c.StartsAt,
            c.ExpiresAt,
            c.JDFileUrl,
            c.CriteriaFileUrl,
            c.Questions
                .Select(q => new CampaignQuestionResponse(
                    q.Id,
                    q.QuestionText,
                    q.Source.ToString(),
                    q.TimeLimitSeconds,
                    q.IsRequired
                )).ToList(),
            c.CreatedAt,
            c.UpdatedAt
        );
    }
}
