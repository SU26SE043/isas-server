namespace Isas.CampaignService.Services
{
    /// <summary>Nhu cầu công việc AIService đề xuất (trước khi map sang <c>JobNeed</c>).</summary>
    public record SuggestedJobNeed(string Category, string Text);

    /// <summary>
    /// Bước 1 của HR technical screener — AIService đọc JD, suy ra công việc cần KIỂU NGƯỜI nào.
    ///
    /// Trả null ⇒ caller giữ nguyên <c>job_needs</c> đang có (KHÔNG xoá): AI chết không phải lý do
    /// để xoá bộ nhu cầu HR đã chốt, và bộ rỗng thì sàng CV không chạy được.
    /// </summary>
    public interface IJobNeedsSuggester
    {
        Task<List<SuggestedJobNeed>?> SuggestAsync(
            string jdText, string? jobCategory, string language, CancellationToken ct = default);
    }
}
