using Isas.InterviewService.Enums;

namespace Isas.InterviewService.Services;

/// <summary>
/// RUB1 · INT-10 — NGUỒN DUY NHẤT gộp điểm từng tiêu chí thành điểm buổi B2C (TRƯỚC hình phạt bỏ câu
/// CAMP-21). Ba nơi gọi, và cả ba phải ra CÙNG một con số cho cùng một buổi:
/// <list type="bullet">
/// <item><see cref="SessionResultService"/> — ghi <c>practice_sessions.overall_score</c> lúc chấm xong;</item>
/// <item><c>PracticeService.MapResult</c> — <c>scoreBeforePenalty</c> trên màn kết quả;</item>
/// <item><see cref="RoadmapReportService"/> — điểm từng buổi trên báo cáo lộ trình.</item>
/// </list>
/// Hai bản công thức ở hai chỗ là cách chắc nhất để màn kết quả nói 72 còn màn lộ trình nói 68 cho
/// cùng một buổi (tiền lệ <c>SkipPenaltyRule.Apply</c> / <c>AnsweredPredicate</c>).
///
/// <para><b>Đầu vào là % ĐÃ làm tròn 2 chữ số</b> (đúng giá trị lưu ở <c>session_criterion_scores</c>)
/// để tổng hợp trên màn hình cộng lại khớp với điểm đã lưu. Chỉ truyền tiêu chí CÓ điểm: tiêu chí
/// không được hỏi rơi khỏi CẢ tử lẫn mẫu (INT-18 — không tính 0 cho thứ không được hỏi).</para>
/// </summary>
public static class B2CScoreFormulaRule
{
    /// <summary>Một tiêu chí CÓ điểm của buổi: % (0–100, đã làm tròn 2) và trọng số của bộ ĐÃ GHIM.</summary>
    public readonly record struct Part(decimal Percentage, decimal Weight);

    /// <param name="formula">
    /// Con dấu của buổi. <see cref="B2CScoreFormula.Weighted"/> ⇒ Σ(pct×w)/Σw. <c>null</c> (buổi trước
    /// RUB1) hoặc <see cref="B2CScoreFormula.Average"/> ⇒ trung bình cộng — y hệt luật cũ từng byte,
    /// để buổi cũ chấm lại (republisher) không đổi điểm.
    /// </param>
    /// <returns>0–100, làm tròn 2. Không tiêu chí nào có điểm ⇒ 0.</returns>
    public static decimal Combine(IReadOnlyCollection<Part> parts, B2CScoreFormula? formula)
    {
        if (parts.Count == 0) return 0m;

        if (formula == B2CScoreFormula.Weighted)
        {
            var weightSum = parts.Sum(p => p.Weight);
            // CHECK ck_rubric_criteria_weight_range (weight > 0) làm ca này không tới được với dữ liệu
            // thật; nếu tới (dữ liệu bẩn) thì lùi về trung bình cộng thay vì chia 0 / trả 0 oan.
            if (weightSum > 0m)
                return Math.Round(
                    Math.Clamp(parts.Sum(p => p.Percentage * p.Weight) / weightSum, 0m, 100m), 2);
        }

        return Math.Round(Math.Clamp(parts.Sum(p => p.Percentage) / parts.Count, 0m, 100m), 2);
    }
}
