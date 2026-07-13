namespace Isas.PaymentService.Services
{
    /// <summary>
    /// BF3 — lỗi cổng thanh toán PayOS: config thiếu (ReturnUrl/CancelUrl chưa set) hoặc PayOS API
    /// từ chối tạo payment-link. Tách khỏi KeyNotFound/InvalidOperation (404/400) để controller map
    /// **502 Bad Gateway** — không nuốt lỗi upstream/misconfig thành lỗi client, không lộ stack thô.
    /// </summary>
    public class PaymentGatewayException : Exception
    {
        public PaymentGatewayException(string message, Exception? inner = null) : base(message, inner) { }
    }
}
