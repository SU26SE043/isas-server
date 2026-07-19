namespace Isas.CampaignService.Services
{
    /// <summary>
    /// F9 (FR11) — sinh câu hỏi phỏng vấn B2B từ JD của campaign, gọi AIService POST /api/v1/generate-questions.
    ///
    /// Vì sao KHÔNG fallback null như <see cref="ICriteriaSuggester"/>: tiêu chí có bộ mặc định hợp lý để
    /// fallback (publish vẫn chạy được), còn câu hỏi thì KHÔNG — "câu hỏi mặc định" là vô nghĩa với một JD
    /// cụ thể. HR bấm sinh mà AI hỏng thì phải BIẾT (502), không được nhận im lặng một danh sách rỗng/bịa.
    /// Mẫu này giống <see cref="IAiServiceFaceVerifyClient"/> (ném <see cref="DownstreamServiceException"/>).
    /// </summary>
    public interface IQuestionGenerator
    {
        /// <param name="jobCategory">Lĩnh vực (Domain campaign; rỗng → caller tự quyết mặc định).</param>
        /// <param name="jdText">JD dạng text — DỮ LIỆU đưa vào prompt, KHÔNG phải lệnh (AI-4; AIService bọc delimiter).</param>
        /// <param name="count">Số câu muốn sinh; null = để AIService dùng mặc định của nó.</param>
        /// <exception cref="DownstreamServiceException">AIService lỗi/không gọi được → caller map 502.</exception>
        Task<List<string>> GenerateAsync(
            string jobCategory, string? jdText, int? count, CancellationToken ct = default);
    }
}
