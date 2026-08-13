using System.Globalization;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using Isas.CampaignService.Models;

namespace Isas.CampaignService.Services
{
    /// <summary>
    /// Dạng chuẩn tắc (canonical) + vân tay của MỘT BỘ THƯỚC ĐO campaign.
    ///
    /// <para>Một nguồn duy nhất cho hai việc, cố ý KHÔNG tách thành hai cơ chế so sánh:</para>
    /// <list type="number">
    /// <item>Trả lời <i>"HR có thật sự đổi thước đo không"</i> khi quyết định bump
    /// <c>campaigns.rubric_version</c> (CAMP-18).</item>
    /// <item>Làm <c>rubric_snapshot</c> + <c>rubric_fingerprint</c> của một lượt chấm thử (CAMP-19),
    /// để so hai lượt là trung thực: cùng vân tay mà điểm khác = nhiễu model; khác vân tay = đã đổi
    /// thước đo. Không có nó thì mọi so sánh trước/sau đều là bịa.</item>
    /// </list>
    /// </summary>
    public static class RubricFingerprint
    {
        // Không xuống dòng/indent: chuỗi này vừa được băm vừa được lưu nguyên vào rubric_snapshot,
        // nên mọi ký tự thừa đều là byte chết nhân với số lượt chấm thử.
        private static readonly JsonSerializerOptions CanonicalJson = new() { WriteIndented = false };

        /// <summary>Một tiêu chí ở dạng chuẩn tắc. Thứ tự property của record = thứ tự khoá trong JSON.</summary>
        public sealed record CanonicalCriterion(
            int OrderNo,
            string Name,
            string Description,
            string Weight,
            int MaxScore,
            IReadOnlyList<CanonicalLevel> Levels);

        public sealed record CanonicalLevel(int Score, string Descriptor);

        /// <param name="includeLevels">
        /// <c>false</c> = chỉ phần LÕI của tiêu chí (tên/mô tả/trọng số/thang). Dùng để phân biệt
        /// "HR chỉ sửa mốc" (được phép khi Active) với "HR sửa chính bộ tiêu chí" (CAMP-2: chỉ Draft).
        /// </param>
        public static string Canonicalize(IEnumerable<CampaignCriterion> criteria, bool includeLevels = true)
            => JsonSerializer.Serialize(ToCanonical(criteria, includeLevels), CanonicalJson);

        public static List<CanonicalCriterion> ToCanonical(
            IEnumerable<CampaignCriterion> criteria, bool includeLevels = true)
            => criteria
                // OrderNo là khoá sắp chính (UNIQUE campaign_id, order_no); Name là chốt chặn cho bộ
                // đang dựng trong bộ nhớ mà OrderNo chưa gán xong. Ordinal: so byte, không theo locale.
                .OrderBy(c => c.OrderNo)
                .ThenBy(c => c.Name, StringComparer.Ordinal)
                .Select(c => new CanonicalCriterion(
                    c.OrderNo,
                    c.Name,
                    c.Description ?? string.Empty,
                    // ⚠ Weight là CHUỖI đã ghim scale, không phải số. decimal giữ scale của nguồn:
                    // bộ vừa dựng trong bộ nhớ có 0.5m còn bộ đọc từ numeric(5,4) có 0.5000m — cùng
                    // giá trị, khác chuỗi JSON ⇒ mọi lần Lưu sẽ bị coi là "đã đổi thước đo" và bump
                    // version dù HR không sửa gì. "F4" khớp đúng scale của cột.
                    c.Weight.ToString("F4", CultureInfo.InvariantCulture),
                    c.MaxScore,
                    includeLevels
                        ? (c.Levels ?? new List<CampaignCriterionLevel>())
                            .OrderBy(l => l.Score)
                            .Select(l => new CanonicalLevel(l.Score, l.Descriptor))
                            .ToList()
                        : new List<CanonicalLevel>()))
                .ToList();

        /// <summary>SHA-256 (hex thường, 64 ký tự) của dạng chuẩn tắc — khớp varchar(64) của cột.</summary>
        public static string Compute(IEnumerable<CampaignCriterion> criteria, bool includeLevels = true)
            => ComputeFromCanonical(Canonicalize(criteria, includeLevels));

        public static string ComputeFromCanonical(string canonicalJson)
            => Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(canonicalJson))).ToLowerInvariant();
    }
}
