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

    /// <summary>
    /// CAMP-20 — <c>GET /campaign/criteria/system-default/preview</c>: Employer XEM TRƯỚC bộ chuẩn
    /// trước khi bấm chép. <b>CHỈ ĐỌC, không ghi gì.</b>
    ///
    /// <para><b>Vì sao phải có:</b> employer không có cửa nào khác đọc được bộ chuẩn —
    /// <c>/internal/rubrics/b2c</c> là máy-máy (<c>X-Internal-Token</c>) còn màn quản trị đòi
    /// <c>Roles="Admin"</c>. Thiếu endpoint này thì hộp thoại chỉ nói được "sẽ thay thế N tiêu chí",
    /// tức employer bấm mù vào đúng thao tác thay cả thước đo — cái mà tính năng này sinh ra để tránh.</para>
    /// </summary>
    public class SystemDefaultRubricPreviewResponse
    {
        public string JobCategory { get; set; } = null!;
        public string Language { get; set; } = null!;

        /// <summary>Phiên bản bộ chuẩn bên Interview — KHÔNG phải <c>campaigns.rubric_version</c>.</summary>
        public int Version { get; set; }

        public List<SystemDefaultRubricCriterionPreview> Criteria { get; set; } = new();
    }

    public class SystemDefaultRubricCriterionPreview
    {
        public string Name { get; set; } = null!;
        public string? Description { get; set; }
        public decimal Weight { get; set; }
        public int MaxScore { get; set; }

        /// <summary>
        /// SỐ mốc. Giữ lại (không thay bằng <see cref="Levels"/>): hộp thoại xem lướt chỉ cần con số,
        /// và FE cũ đọc field này không vỡ.
        ///
        /// <para><c>0</c> = admin CHƯA khai mốc cho tiêu chí này ⇒ chép về vẫn hợp lệ, Interview rơi về
        /// dải mặc định (CAMP-14). FE nên hiện badge "chưa có mốc" thay vì coi là lỗi.</para>
        /// </summary>
        public int LevelCount { get; set; }

        /// <summary>
        /// RNK1 · HĐ-4 — nội dung mốc (Score + Descriptor, sắp theo Score tăng dần). Employer thấy
        /// TRƯỚC khi bấm chép mình sắp nhận thang điểm nào, thay vì chép mù rồi mới xem.
        ///
        /// <para>LUÔN là list (không bao giờ null): admin chưa soạn mốc ⇒ <c>[]</c> (và
        /// <see cref="LevelCount"/> = 0). Cùng nguồn với đường chép (<c>ApplySystemDefaultCriteriaAsync</c>)
        /// nên "xem trước" khớp đúng "sẽ chép".</para>
        /// </summary>
        public List<CriterionLevelResponse> Levels { get; set; } = new();
    }
}
