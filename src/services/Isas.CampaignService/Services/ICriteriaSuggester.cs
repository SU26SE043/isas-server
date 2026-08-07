namespace Isas.CampaignService.Services
{
    /// <summary>Tiêu chí AIService đề xuất (trước khi map sang CampaignCriterion).</summary>
    public record SuggestedCriterion(string Name, string? Description, decimal Weight, int MaxScore);

    /// <summary>Gọi AIService đề xuất tiêu chí có cấu trúc (C8). Trả null/rỗng → caller fallback default.</summary>
    public interface ICriteriaSuggester
    {
        Task<List<SuggestedCriterion>?> SuggestAsync(
            string jobCategory, string? jdText, string? criteriaText, int count, string language = "vi", CancellationToken ct = default);
    }
}
