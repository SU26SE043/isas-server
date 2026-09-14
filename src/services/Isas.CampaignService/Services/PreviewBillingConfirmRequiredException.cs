namespace Isas.CampaignService.Services
{
    /// <summary>
    /// REV-BE R3 (I7 ở tầng tiền) — lượt chấm thử này SẼ trừ 1 credit tổ chức (quota free của
    /// (campaign, rubricVersion, câu) đã hết) mà client chưa xác nhận (<c>confirmBilled = false</c>).
    ///
    /// <para>Vì sao ở BE chứ không tin FE: FE quyết "hỏi trước khi trừ" bằng lịch sử <c>.Take(20)</c> và
    /// lúc history đang tải (<c>runs = []</c>) nó tưởng còn lượt free ⇒ POST không hỏi ⇒ BE trừ credit.
    /// Với tới khi ≥21 lượt/campaign, bấm nhanh lúc đang tải, hoặc cửa sổ deploy lệch FE/BE. Ném TRƯỚC
    /// khi insert row Running và TRƯỚC <c>ReserveAsync</c> ⇒ không row rác, không chạm Payment.</para>
    ///
    /// <para>Controller map → <b>409</b> body <c>{ code: "PREVIEW_BILLING_CONFIRM_REQUIRED",
    /// freeRunsRemaining: 0, questionId }</c>. FE hiện hộp thoại "lượt này trừ 1 credit" rồi POST lại với
    /// <c>confirmBilled: true</c>.</para>
    /// </summary>
    public sealed class PreviewBillingConfirmRequiredException(Guid questionId)
        : Exception("Lượt chấm thử này sẽ trừ 1 credit tổ chức — gửi confirmBilled = true để xác nhận.")
    {
        public Guid QuestionId { get; } = questionId;

        /// <summary>Body 409 — khoá JSON camelCase: <c>code · freeRunsRemaining · questionId</c>.</summary>
        public object Body { get; } = new
        {
            code = "PREVIEW_BILLING_CONFIRM_REQUIRED",
            freeRunsRemaining = 0,
            questionId
        };
    }
}
