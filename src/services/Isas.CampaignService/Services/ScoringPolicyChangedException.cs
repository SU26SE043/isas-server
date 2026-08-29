namespace Isas.CampaignService.Services
{
    /// <summary>
    /// SCP1 · B8 / HĐ-4 — vân tay <c>fingerprint</c> ở body <c>apply</c> KHÔNG khớp vân tay tính lại từ
    /// dòng chính sách đã lưu ⇒ ai đó đã đổi biểu thức/ngưỡng giữa lúc HR xem trước và lúc bấm áp ⇒
    /// bảng tác động HR vừa xem KHÔNG còn đúng. Controller map → <c>409</c> với mã
    /// <c>POLICY_CHANGED_AFTER_PREVIEW</c>.
    ///
    /// <para>KHÔNG dẫn xuất từ <see cref="InvalidOperationException"/> — đường apply map loại đó → 400
    /// (chưa có ai được chấm). Bắt riêng trước.</para>
    /// </summary>
    public sealed class ScoringPolicyChangedException()
        : Exception("POLICY_CHANGED_AFTER_PREVIEW: chính sách đã đổi sau khi xem trước — hãy xem lại rồi áp.");
}
