namespace Isas.CampaignService.DTOs
{
    /// <summary>
    /// CMP3-B3 — kết quả <c>POST /campaign/{id}/job-needs/suggest</c>: AI đọc JD, đề xuất bộ NHU CẦU
    /// CÔNG VIỆC để HR chốt TRƯỚC khi sàng CV (sàng CV đòi <c>job_needs</c> — CMP3-B1).
    ///
    /// <para><b>CHỈ ĐỌC.</b> Endpoint KHÔNG ghi <c>campaigns.job_needs</c> — HR lưu qua
    /// <c>PUT /campaign/{id}/job-needs</c>. Một cửa ghi duy nhất (mẫu <c>POST /questions/import</c>,
    /// <c>POST /criteria/levels/suggest</c>): validate/audit/luật chốt nằm đúng một chỗ.</para>
    /// </summary>
    public class SuggestJobNeedsResponse
    {
        public List<SuggestedJobNeedItem> JobNeeds { get; set; } = new();
    }

    /// <summary>
    /// Một nhu cầu công việc AI đề xuất. KHÔNG có <c>needId</c>: id do server cấp khi HR lưu qua
    /// <c>PUT /job-needs</c> (id sinh ở đây chết ngay lần HR sửa đầu tiên — cùng lý do
    /// <see cref="Services.CampaignService"/> tự cấp <c>NeedId</c> trong <c>BuildJobNeedsAsync</c>).
    /// </summary>
    public class SuggestedJobNeedItem
    {
        public string Category { get; set; } = null!;   // Technical | WorkStyle | Communication | Growth
        public string Text { get; set; } = null!;

        /// <summary>Luôn <c>"AiSuggested"</c> — nhãn nguồn gốc do SERVER sở hữu (bài học F10). HR gõ
        /// tay đi qua <c>PUT /job-needs</c> và nhận nhãn <c>"HrEdited"</c> ở đó.</summary>
        public string Source { get; set; } = null!;

        /// <summary>Luôn <c>false</c> — điều kiện LOẠI (HĐ-6) là quyết định nghiệp vụ của HR, AI
        /// không đề xuất.</summary>
        public bool IsMustHave { get; set; }
    }
}
