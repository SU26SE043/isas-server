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
        DateTime? RefundedAt,
        // Mốc xác nhận tiền đã chuyển về khách. NULL = chờ chuyển tiền (xem Order.RefundSettledAt).
        DateTime? RefundSettledAt = null)
    {
        public static RefundResult Simple(RefundOutcome outcome, Guid orderId) =>
            new(outcome, orderId, 0, 0, 0, 0, null, null);
    }

    /// <summary>Kết cục lệnh "xác nhận đã chuyển tiền hoàn" (settle) — bước tay tách khỏi refund.</summary>
    public enum SettleOutcome
    {
        /// <summary>Vừa đánh dấu đã chuyển tiền thành công.</summary>
        Settled,
        /// <summary>Đơn đã được đánh dấu chuyển tiền từ trước (idempotent — không đổi mốc cũ).</summary>
        AlreadySettled,
        /// <summary>Không tìm thấy đơn.</summary>
        OrderNotFound,
        /// <summary>Đơn chưa ở trạng thái Refunded — chưa hoàn thì không có gì để xác nhận chuyển tiền.</summary>
        NotRefunded
    }

    /// <summary>Kết quả settle: mốc hoàn + mốc chuyển tiền + mã tham chiếu hiện có.</summary>
    public sealed record SettleRefundResult(
        SettleOutcome Outcome,
        Guid OrderId,
        DateTime? RefundedAt,
        DateTime? RefundSettledAt,
        string? RefundGatewayRef)
    {
        public static SettleRefundResult Simple(SettleOutcome outcome, Guid orderId) =>
            new(outcome, orderId, null, null, null);
    }
}
