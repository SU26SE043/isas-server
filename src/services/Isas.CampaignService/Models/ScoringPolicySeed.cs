using Isas.Shared.Scoring;

namespace Isas.CampaignService.Models
{
    /// <summary>
    /// SCP1 · HĐ-3 §4 — NĂM mẫu hệ thống (<c>campaign_id = NULL</c>), nạp qua <c>HasData</c>.
    ///
    /// <para>GUID và <see cref="ScoringPolicy.CreatedAt"/> ghim cứng để <c>HasData</c> tất định — mỗi
    /// lần <c>migrations add</c> không sinh <c>UpdateData</c> oan. <see cref="ScoringPolicy.EngineVersion"/>
    /// lấy từ <see cref="ScoringEngine.Version"/> (đổi hằng đó ⇒ mẫu seed lần deploy sau mang số mới —
    /// đúng ý đồ HĐ-4).</para>
    ///
    /// <para><c>pass_score_pct</c> mẫu: <b>Phỏng vấn 60 · Sàng CV 50</b> — điểm khởi đầu hợp lý để HR
    /// chỉnh, không phải chuẩn ngành. HĐ-3 §4 không quy định con số này nên chọn ở đây; HR đổi được sau
    /// (đây chỉ là mẫu để chép).</para>
    /// </summary>
    internal static class ScoringPolicySeed
    {
        private static readonly DateTime At = new(2026, 8, 13, 0, 0, 0, DateTimeKind.Utc);

        public static readonly ScoringPolicy[] Templates =
        [
            new()
            {
                Id = new Guid("5c900001-0000-0000-0000-000000000000"),
                CampaignId = null,
                Kind = ScoringExpressionKind.Interview,
                Version = 1,
                EngineVersion = ScoringEngine.Version,
                Name = "Như hiện nay",
                Description = "Điểm tổng có trọng số của các tiêu chí — đúng công thức hệ thống đang dùng.",
                Expression = "weighted_avg_pct",
                PassScorePct = 60,
                SourceTemplateId = null,
                CreatedAt = At,
                CreatedBy = null,
            },
            new()
            {
                Id = new Guid("5c900002-0000-0000-0000-000000000000"),
                CampaignId = null,
                Kind = ScoringExpressionKind.Interview,
                Version = 1,
                EngineVersion = ScoringEngine.Version,
                Name = "Phạt bỏ câu",
                Description = "Điểm tổng có trọng số nhân với tỷ lệ câu đã trả lời (0..1): bỏ càng nhiều câu điểm càng giảm.",
                Expression = "weighted_avg_pct * completeness",
                PassScorePct = 60,
                SourceTemplateId = null,
                CreatedAt = At,
                CreatedBy = null,
            },
            new()
            {
                Id = new Guid("5c900003-0000-0000-0000-000000000000"),
                CampaignId = null,
                Kind = ScoringExpressionKind.Interview,
                Version = 1,
                EngineVersion = ScoringEngine.Version,
                Name = "Không bù trừ",
                Description = "Có tiêu chí nào dưới 40 thì lấy đúng điểm tiêu chí thấp nhất (không cho điểm mạnh bù điểm yếu); ngược lại lấy điểm tổng có trọng số.",
                Expression = "if(min_pct < 40, min_pct, weighted_avg_pct)",
                PassScorePct = 60,
                SourceTemplateId = null,
                CreatedAt = At,
                CreatedBy = null,
            },
            new()
            {
                Id = new Guid("5c900004-0000-0000-0000-000000000000"),
                CampaignId = null,
                Kind = ScoringExpressionKind.CvScreening,
                Version = 1,
                EngineVersion = ScoringEngine.Version,
                Name = "Như hiện nay",
                Description = "Tỷ lệ nhu cầu đạt: mỗi nhu cầu Strong tính 1, Partial tính 0.5, chia tổng số nhu cầu rồi nhân 100.",
                Expression = "100 * (strong_count + 0.5 * partial_count) / need_count",
                PassScorePct = 50,
                SourceTemplateId = null,
                CreatedAt = At,
                CreatedBy = null,
            },
            new()
            {
                Id = new Guid("5c900005-0000-0000-0000-000000000000"),
                CampaignId = null,
                Kind = ScoringExpressionKind.CvScreening,
                Version = 1,
                EngineVersion = ScoringEngine.Version,
                Name = "Bắt buộc must-have",
                Description = "Thiếu bất kỳ nhu cầu must-have nào → 0 điểm; đủ must-have thì tính như 'Như hiện nay'.",
                Expression = "if(must_have_met < must_have_total, 0, 100 * (strong_count + 0.5 * partial_count) / need_count)",
                PassScorePct = 50,
                SourceTemplateId = null,
                CreatedAt = At,
                CreatedBy = null,
            },
        ];
    }
}
