using Isas.CampaignService.Models;

namespace Isas.CampaignService.Services
{
    /// <summary>
    /// RNK1 · HĐ-6 — điều kiện LOẠI ở vòng sàng CV. NGUỒN TÍNH DUY NHẤT của <c>must_have_*</c>: cả
    /// đường chấm LIVE (<see cref="CvScreeningService"/>) lẫn đường xem trước / áp chính sách
    /// (<see cref="ScoringPolicyService"/>) gọi đúng hàm này — hai đoạn nhân riêng sẽ trôi xa nhau
    /// mà không lỗi nào nổ (cùng lớp bẫy <c>SkipPenaltyRule</c> của HĐ-2).
    ///
    /// <para><b>Đủ điều kiện</b> ⇔ MỌI nhu cầu <c>IsMustHave</c> có bằng chứng Strong/Partial. Nhu
    /// cầu must-have bị Weak (gap) HOẶC chưa có assessment nào ⇒ <c>Missing</c> ⇒ ứng viên bị loại.
    /// 0 must-have ⇒ <c>Eligible = true</c> (HR chưa khai điều kiện loại thì không loại ai).</para>
    ///
    /// <para>Đánh giá READ-TIME từ (<c>job_needs</c> hiện tại ∩ <c>strengths</c>/<c>gaps</c> đã lưu)
    /// — KHÔNG cột, KHÔNG ghim, KHÔNG gọi lại <c>screen_cv</c>. Ổn định vì <c>job_needs</c> bị khoá
    /// sau khi có người sàng (<see cref="ScoringPolicyService"/>/<c>ReplaceJobNeedsAsync</c> → 409).</para>
    /// </summary>
    public static class CvMustHaveEvaluator
    {
        /// <param name="Missing">Nhu cầu must-have CHƯA đạt (gap hoặc chưa đánh giá) — HR đọc "thiếu điều kiện gì".</param>
        public sealed record Result(
            bool Eligible,
            IReadOnlyList<JobNeed> Missing,
            int MustHaveMet,
            int MustHaveTotal);

        /// <param name="strengths">Assessment mức Strong/Partial (<c>CvSubmission.Strengths</c>).</param>
        /// <param name="gaps">Assessment mức Weak (<c>CvSubmission.Gaps</c>) — không dùng trực tiếp,
        /// "chưa đạt" = "không có trong strengths" đã phủ cả gap lẫn chưa-đánh-giá.</param>
        public static Result Evaluate(
            IEnumerable<JobNeed>? jobNeeds,
            IEnumerable<NeedAssessment>? strengths,
            IEnumerable<NeedAssessment>? gaps)
        {
            var mustHaves = (jobNeeds ?? Enumerable.Empty<JobNeed>())
                .Where(n => n.IsMustHave && !string.IsNullOrWhiteSpace(n.NeedId))
                .ToList();

            if (mustHaves.Count == 0)
                return new Result(Eligible: true, Missing: Array.Empty<JobNeed>(), MustHaveMet: 0, MustHaveTotal: 0);

            // "Đạt" = có needId trong strengths (Strong/Partial). "Không đạt" phủ CẢ HAI ca —
            // needId nằm trong gaps (Weak), và needId không xuất hiện assessment nào (quên đánh giá).
            var met = new HashSet<string>(
                (strengths ?? Enumerable.Empty<NeedAssessment>())
                    .Select(a => a.NeedId)
                    .Where(id => !string.IsNullOrWhiteSpace(id)),
                StringComparer.Ordinal);

            var missing = mustHaves.Where(n => !met.Contains(n.NeedId)).ToList();

            return new Result(
                Eligible: missing.Count == 0,
                Missing: missing,
                MustHaveMet: mustHaves.Count - missing.Count,
                MustHaveTotal: mustHaves.Count);
        }
    }
}
