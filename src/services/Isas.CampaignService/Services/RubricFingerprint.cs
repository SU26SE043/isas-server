using Isas.CampaignService.Models;
using Isas.Shared.Rubric;

namespace Isas.CampaignService.Services
{
    /// <summary>
    /// Vân tay bộ thước đo của campaign — <b>lớp mỏng</b> map <see cref="CampaignCriterion"/> sang
    /// <see cref="RubricCriterionSnapshot"/> rồi gọi <see cref="Isas.Shared.Rubric.RubricFingerprint"/>.
    ///
    /// <para><b>Vì sao thuật toán nằm ở Shared:</b> cùng phép băm này còn được dùng cho bộ chuẩn B2C
    /// (InterviewService) và để đối chiếu bộ đã materialize với bộ gốc. Hai bản cài đặt riêng nghĩa là
    /// cùng một bộ thước đo băm ra hai vân tay khác nhau ⇒ "cùng thước đo hay khác" trả lời sai, mà
    /// đó lại chính là câu hỏi mọi so sánh trước/sau đứng trên.</para>
    /// </summary>
    public static class RubricFingerprint
    {
        private static IEnumerable<RubricCriterionSnapshot> ToSnapshots(IEnumerable<CampaignCriterion> criteria)
            => criteria.Select(c => new RubricCriterionSnapshot(
                c.OrderNo,
                c.Name,
                c.Description,
                c.Weight,
                c.MaxScore,
                (c.Levels ?? new List<CampaignCriterionLevel>())
                    .Select(l => new RubricLevelSnapshot(l.Score, l.Descriptor))
                    .ToList()));

        /// <param name="includeLevels">
        /// <c>false</c> = chỉ phần LÕI (tên/mô tả/trọng số/thang). Dùng để phân biệt "HR chỉ sửa mốc"
        /// (được phép khi Active) với "HR sửa chính bộ tiêu chí" (CAMP-2: chỉ Draft).
        /// </param>
        public static string Canonicalize(IEnumerable<CampaignCriterion> criteria, bool includeLevels = true)
            => Isas.Shared.Rubric.RubricFingerprint.Canonicalize(ToSnapshots(criteria), includeLevels);

        /// <summary>SHA-256 (hex thường, 64 ký tự) của dạng chuẩn tắc — khớp varchar(64) của cột.</summary>
        public static string Compute(IEnumerable<CampaignCriterion> criteria, bool includeLevels = true)
            => Isas.Shared.Rubric.RubricFingerprint.Compute(ToSnapshots(criteria), includeLevels);

        public static string ComputeFromCanonical(string canonicalJson)
            => Isas.Shared.Rubric.RubricFingerprint.ComputeFromCanonical(canonicalJson);
    }
}
