namespace Isas.PaymentService.Services
{
    /// <summary>F18 — kết cục của một lệnh hoàn tiền (map sang HTTP ở controller).</summary>
    public enum RefundOutcome
    {
        /// <summary>Hoàn thành công (có thể kèm thu hồi credit một phần / bằng 0).</summary>
        Refunded,

        /// <summary>Không tìm thấy đơn.</summary>
        OrderNotFound,

        /// <summary>Đơn đã ở trạng thái Refunded từ trước (idempotent — không làm gì thêm).</summary>
        AlreadyRefunded,

        /// <summary>Đơn chưa Paid (Pending/Expired/Cancelled/Failed) — chưa thu tiền thì không có gì để hoàn.</summary>
        NotPaid,

        /// <summary>Loại đơn chưa hỗ trợ hoàn (xem <see cref="IRefundService"/>).</summary>
        UnsupportedKind,

        /// <summary>
        /// Ví không còn đủ credit để thu hồi trọn khoản đã mua, mà lệnh gọi KHÔNG cho phép thu hồi một phần.
        /// </summary>
        InsufficientCredits,

        /// <summary>
        /// Số dư ví vừa đổi ngay giữa lúc hoàn (ai đó tiêu/giữ credit cùng lúc) → huỷ toàn bộ, gọi lại.
        /// </summary>
        WalletChanged
    }

    /// <summary>
    /// F18 — kết quả hoàn tiền. <see cref="CreditsPurchased"/> vs <see cref="CreditsClawedBack"/> là hai
    /// con số PHẢI khác nhau được: ví đã tiêu hết thì phần thu hồi được nhỏ hơn phần đã bán, và cái chênh
    /// đó chính là khoản lỗ mà người duyệt hoàn cần nhìn thấy trước khi bấm.
    /// </summary>
    public sealed record RefundResult(
        RefundOutcome Outcome,
        Guid OrderId,
        long AmountVnd,
        int CreditsPurchased,
        int CreditsClawedBack,
        int ClawbackCeiling,
        Guid? RefundTransactionId,
        DateTime? RefundedAt)
    {
        public static RefundResult Simple(RefundOutcome outcome, Guid orderId) =>
            new(outcome, orderId, 0, 0, 0, 0, null, null);
    }
}
