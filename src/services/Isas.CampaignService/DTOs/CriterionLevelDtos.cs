namespace Isas.CampaignService.DTOs
{
    /// <summary>
    /// CAMP-16 — kết quả <c>POST /campaign/{id}/criteria/levels/suggest</c>.
    ///
    /// <para><b>CHỈ ĐỌC.</b> Endpoint này KHÔNG ghi DB: HR xem/sửa rồi bấm Lưu, và việc lưu đi qua đúng
    /// một cửa <c>PUT /campaign/{id}</c> — nhờ vậy validate CAMP-17, audit và luật bump version chỉ nằm
    /// ở một chỗ. Cùng nguyên tắc với <c>POST /questions/import</c>.</para>
    /// </summary>
    public class SuggestCriterionLevelsResponse
    {
        public List<SuggestedCriterionLevels> Criteria { get; set; } = new();
    }

    public class SuggestedCriterionLevels
    {
        public Guid CriterionId { get; set; }
        // Echo tên + thang điểm để FE dựng bảng ngay từ response, khỏi tra chéo campaign detail.
        public string Name { get; set; } = null!;
        public int MaxScore { get; set; }
        public List<CriterionLevelResponse> Levels { get; set; } = new();
    }
}
