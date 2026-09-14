using Isas.Shared.Scoring;

namespace Isas.CampaignService.Services
{
    /// <summary>
    /// SCP1 · B4 — biểu thức chính sách không qua được <c>ScoringExpression.Validate</c> ở đường tạo
    /// version. Mang danh sách <see cref="ScoringError"/> (MÃ + [start,end)) để controller trả 400 với
    /// body <c>{ "errors": [...] }</c> — cùng hình dạng lỗi HĐ-2 mà FE đã xử ở B3.
    ///
    /// <para>KHÔNG dẫn xuất từ <see cref="InvalidOperationException"/> (controller map loại đó → 409).</para>
    /// </summary>
    public sealed class ScoringExpressionInvalidException(IReadOnlyList<ScoringError> errors)
        : Exception("Biểu thức chính sách không hợp lệ.")
    {
        public IReadOnlyList<ScoringError> Errors { get; } = errors;
    }
}
