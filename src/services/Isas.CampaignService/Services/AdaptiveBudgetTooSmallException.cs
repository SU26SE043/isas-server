using Isas.CampaignService.Validation;

namespace Isas.CampaignService.Services
{
    /// <summary>
    /// RNK1 · HĐ-7 — ràng buộc chéo adaptive lệch: trần buổi <c>T</c> không đủ cho MỌI chuỗi đào sâu
    /// tối đa (<c>K × (1 + d)</c>). Controller trả <b>400</b> body
    /// <c>{ "code": "ADAPTIVE_BUDGET_TOO_SMALL", "need", "have", "questions", "deep" }</c> —
    /// KHÔNG toast mã lỗi chung.
    ///
    /// <para>KHÔNG dẫn xuất <see cref="System.ArgumentException"/> / <see cref="System.InvalidOperationException"/>
    /// (controller map hai loại đó → 400-string / 409). Mẫu <see cref="ScoringExpressionInvalidException"/>:
    /// mỗi action liên quan bắt riêng loại này TRƯỚC các catch generic.</para>
    /// </summary>
    public sealed class AdaptiveBudgetTooSmallException(AdaptiveBudgetRule.Violation v)
        : Exception(
            $"Ngân sách buổi ({v.Have} câu) không đủ: {v.Questions} câu gốc × (1 + {v.Deep} đào sâu) "
            + $"= {v.Need} câu. Tăng max_questions ≥ {v.Need} hoặc giảm max_deep_per_question.")
    {
        /// <summary>Body 400 — khoá JSON camelCase khớp HĐ-7: <c>code · need · have · questions · deep</c>.</summary>
        public object Body { get; } = new
        {
            code = AdaptiveBudgetRule.Code,
            need = v.Need,
            have = v.Have,
            questions = v.Questions,
            deep = v.Deep,
        };
    }
}
