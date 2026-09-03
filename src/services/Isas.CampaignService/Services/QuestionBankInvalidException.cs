namespace Isas.CampaignService.Services
{
    /// <summary>
    /// RNK1 · HĐ-8 — publish một campaign mà ngân hàng đề có cảnh báo (K &gt; số câu · số câu bắt buộc
    /// &gt; K · ngân sách adaptive không đủ). Controller trả <b>400</b> body
    /// <c>{ "code": "QUESTION_BANK_INVALID", "warnings": [...] }</c> — KHÔNG toast mã lỗi chung.
    ///
    /// <para>KHÔNG dẫn xuất <see cref="System.ArgumentException"/> / <see cref="System.InvalidOperationException"/>
    /// (controller map hai loại đó → 400-string / 409). Mẫu
    /// <see cref="ScoringExpressionInvalidException"/> / <see cref="AdaptiveBudgetTooSmallException"/>:
    /// <c>PublishCampaign</c> bắt riêng loại này TRƯỚC các catch generic.</para>
    /// </summary>
    public sealed class QuestionBankInvalidException(IReadOnlyList<string> warnings)
        : Exception("Ngân hàng đề chưa hợp lệ để publish: " + string.Join(" · ", warnings))
    {
        public IReadOnlyList<string> Warnings { get; } = warnings;

        /// <summary>Body 400 — khoá JSON camelCase: <c>code · warnings</c>.</summary>
        public object Body { get; } = new { code = "QUESTION_BANK_INVALID", warnings };
    }
}
