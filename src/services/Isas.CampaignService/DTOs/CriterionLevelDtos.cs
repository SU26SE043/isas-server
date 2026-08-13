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

    /// <summary>
    /// CAMP-20 — <c>POST /campaign/{id}/criteria/from-system-default</c>: chép bộ chuẩn B2C (admin
    /// soạn) vào campaign, THAY THẾ toàn bộ tiêu chí đang có.
    ///
    /// <para>Cả hai trường đều BẮT BUỘC. Server KHÔNG suy nghề từ <c>campaigns.domain</c> — cột đó là
    /// chuỗi tự do đang chứa cả <c>"Fullstack"</c>/<c>"QA"</c>/<c>null</c>, và đoán sai ở đây nghĩa là
    /// chiến dịch được chấm bằng thước của nghề khác mà không có triệu chứng nào.</para>
    ///
    /// <para>⚠ Chép về sẽ mang cả MỐC ĐIỂM của bộ chuẩn và <b>xoá mốc HR đang có</b> — thay thước đo
    /// thì mốc của thước cũ không còn nghĩa. FE phải xác nhận trước khi gọi.</para>
    /// </summary>
    public class ApplySystemDefaultCriteriaRequest
    {
        /// <summary>BA | BE | FE. Nhận HOA/thường tuỳ ý, server chuẩn hoá.</summary>
        public string? JobCategory { get; set; }

        /// <summary>vi | en. Không có mặc định — xem docblock <c>ApplySystemDefaultCriteriaAsync</c>.</summary>
        public string? Language { get; set; }
    }
}
