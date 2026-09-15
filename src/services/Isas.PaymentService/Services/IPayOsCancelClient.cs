namespace Isas.PaymentService.Services
{
    /// <summary>
    /// Huỷ payment-link PayOS (mockable). Tách khỏi <see cref="IPayOsQueryClient"/> (chỉ đọc) để test
    /// đường huỷ đơn trên SQLite mà KHÔNG chạm network và KHÔNG phải sửa mọi stub query hiện có.
    /// Impl thật <see cref="PayOsCancelClient"/> bọc <c>payOS.PaymentRequests.CancelAsync</c>.
    /// </summary>
    public interface IPayOsCancelClient
    {
        /// <summary>
        /// Huỷ link theo <paramref name="orderCode"/>. PayOS từ chối / lỗi mạng → ném
        /// <see cref="PaymentGatewayException"/> (không để exception SDK văng thành 500).
        /// </summary>
        Task CancelPaymentLinkAsync(long orderCode, string reason, CancellationToken ct = default);
    }
}
