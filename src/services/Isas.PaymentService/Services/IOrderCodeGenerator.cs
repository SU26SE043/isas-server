namespace Isas.PaymentService.Services
{
    /// <summary>
    /// P7 — sinh <c>orders.payos_order_code</c>: time+random, duy nhất, trong trần PayOS.
    /// Xem docs/services/payment.md §PayOS + §order_code, decisions.md D12.
    /// </summary>
    public interface IOrderCodeGenerator
    {
        /// <summary>
        /// Sinh 1 order_code chưa tồn tại trong bảng orders. Đụng UNIQUE(payos_order_code)
        /// → regenerate + retry (bounded). Ném <see cref="InvalidOperationException"/> nếu
        /// hết lượt retry mà vẫn đụng (cực hiếm — không gian số rất lớn).
        /// </summary>
        Task<long> GenerateAsync(CancellationToken ct = default);
    }
}
