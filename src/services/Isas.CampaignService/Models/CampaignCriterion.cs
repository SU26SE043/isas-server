namespace Isas.CampaignService.Models
{
    /// <summary>
    /// Tiêu chí chấm CÓ CẤU TRÚC (D9) — sinh khi publish (AI đề xuất / HR sửa).
    /// Σ Weight của 1 campaign = 1. Khi tạo session → gửi sang Interview materialize rubric.
    /// </summary>
    public class CampaignCriterion
    {
        public Guid Id { get; set; }
        public Guid CampaignId { get; set; }
        public int OrderNo { get; set; }           // C12: thứ tự hiển thị (HR sắp); UNIQUE (campaign_id, order_no)
        public string Name { get; set; } = null!;
        public string? Description { get; set; }
        public decimal Weight { get; set; }        // numeric(5,4) — Σ/campaign = 1
        public int MaxScore { get; set; }
        public CriterionSource Source { get; set; }
        public DateTime CreatedAt { get; set; }
        public DateTime UpdatedAt { get; set; }     // C12

        // Navigation
        public Campaign Campaign { get; set; } = null!;

        /// <summary>
        /// CAMP-16/17 — mốc điểm (E9 hard-anchor). Rỗng = chưa khai mốc ⇒ Interview rơi về dải mặc định
        /// như trước tính năng này (hành vi cũ giữ nguyên, không phải trạng thái lỗi).
        /// </summary>
        public ICollection<CampaignCriterionLevel> Levels { get; set; } = new List<CampaignCriterionLevel>();
    }

    /// <summary>
    /// Nguồn gốc tiêu chí — <b>sự thật do SERVER sở hữu</b> (mẫu F10 cho <c>QuestionSource</c>): giá trị
    /// client gửi lên bị bỏ qua, mỗi đường ghi tự đóng dấu nguồn của nó.
    /// </summary>
    public enum CriterionSource
    {
        /// <summary>AIService <c>/suggest-criteria</c> thật sự sinh ra bộ này.</summary>
        AiSuggested = 0,

        /// <summary>HR khai/sửa trực tiếp qua <c>PUT /campaign/{id}</c>.</summary>
        HrEdited = 1,

        /// <summary>
        /// Bộ do HỆ THỐNG cấp, KHÔNG phải AI: (a) chép từ bộ chuẩn B2C admin soạn
        /// (<c>POST /criteria/from-system-default</c>), (b) bộ dự phòng <c>BuildDefaultCriteria</c> khi
        /// AIService lỗi lúc publish.
        ///
        /// <para>Giá trị THỨ BA, KHÔNG thay <see cref="AiSuggested"/> — hàng cũ vẫn hợp lệ, không backfill.</para>
        ///
        /// <para><b>Vì sao tách khỏi <see cref="AiSuggested"/>:</b> ba tiêu chí dự phòng là hằng số viết
        /// tay trong code, AI chưa từng chạm vào; gắn nhãn "AI đề xuất" khiến HR tin nó hơn mức đáng
        /// tin — thước đo đọc như thứ đã được cân nhắc theo JD của họ, trong khi nó giống hệt nhau ở
        /// mọi chiến dịch.</para>
        /// </summary>
        SystemDefault = 2
    }
}
