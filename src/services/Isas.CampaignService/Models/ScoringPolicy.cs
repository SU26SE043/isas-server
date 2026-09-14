using Isas.Shared.Scoring;

namespace Isas.CampaignService.Models
{
    /// <summary>
    /// SCP1 · HĐ-3 — MỘT BẢN chính sách chấm điểm (biểu thức + ngưỡng đạt).
    ///
    /// <para><b>Bất biến ngay sau khi INSERT</b> — chỉ <see cref="Name"/> và <see cref="Description"/>
    /// sửa được. Trường ngữ nghĩa (<see cref="Expression"/>, <see cref="PassScorePct"/>,
    /// <see cref="EngineVersion"/>) và cả bộ định danh (<see cref="CampaignId"/>, <see cref="Kind"/>,
    /// <see cref="Version"/>, <see cref="CreatedAt"/>, <see cref="CreatedBy"/>,
    /// <see cref="SourceTemplateId"/>) bị EF chặn cập nhật (<c>PropertySaveBehavior.Throw</c> trong
    /// DbContext). Muốn đổi biểu thức/ngưỡng ⇒ POST tạo <see cref="Version"/> MỚI.</para>
    ///
    /// <para><b>KHÔNG có cột <c>is_active</c>.</b> "Đang dùng" = <see cref="Version"/> trùng con trỏ
    /// <c>campaigns.interview_policy_version</c> / <c>cv_policy_version</c>. Thêm cờ active là dựng lại
    /// đúng lớp bug đã cắn rubric một lần — xem <c>RubricCriteriaLoader.cs:81-92</c> (Interview): bộ bị
    /// hạ cờ nhưng buổi ghim nó vẫn phải dùng được để chấm nốt.</para>
    /// </summary>
    public class ScoringPolicy
    {
        public Guid Id { get; set; }

        /// <summary><c>NULL</c> = MẪU hệ thống (seed, HĐ-3). Có giá trị = bản riêng của campaign đó.</summary>
        public Guid? CampaignId { get; set; }

        /// <summary>Phỏng vấn hay Sàng CV — quyết định tập biến hợp lệ (HĐ-1). Lưu chuỗi.</summary>
        public ScoringExpressionKind Kind { get; set; }

        /// <summary>Số phiên bản, tăng dần trong phạm vi <c>(campaign_id, kind)</c>. Mẫu hệ thống = 1.</summary>
        public int Version { get; set; }

        /// <summary>Phiên bản bộ đánh giá lúc tạo (<see cref="ScoringEngine.Version"/>). Vào vân tay HĐ-4.</summary>
        public string EngineVersion { get; set; } = null!;

        public string Name { get; set; } = null!;
        public string? Description { get; set; }

        /// <summary>Biểu thức theo ngôn ngữ HĐ-1. Kiểm hợp lệ ở đường tạo (B3), KHÔNG ở tầng DB.</summary>
        public string Expression { get; set; } = null!;

        /// <summary>Ngưỡng % để auto Đạt/Không đạt. <c>NULL</c> = HR quyết tay (như <c>campaigns.pass_score_pct</c>).</summary>
        public int? PassScorePct { get; set; }

        /// <summary>Mẫu hệ thống mà bản này chép ra (ref lỏng → <c>scoring_policies.id</c>). <c>NULL</c> nếu
        /// tự soạn hoặc chính là mẫu.</summary>
        public Guid? SourceTemplateId { get; set; }

        public DateTime CreatedAt { get; set; }

        /// <summary>User sub của HR tạo bản này. <c>NULL</c> cho mẫu hệ thống (seed).</summary>
        public Guid? CreatedBy { get; set; }
    }
}
