namespace Isas.PaymentService.Models
{
    /// <summary>
    /// Cấu hình billing (payment.md §Vai trò — "Đơn giá 1 lượt = biến cấu hình, cần cho hóa đơn postpaid").
    /// Bind section <c>Billing</c>. <see cref="UnitPrice"/> là đơn giá 1 lượt phỏng vấn (VND) dùng để lập
    /// hóa đơn postpaid cuối kỳ (amount = interview_count × unit_price). Snapshot vào invoice lúc chốt kỳ
    /// → đổi giá sau không hồi tố hóa đơn đã lập.
    /// </summary>
    public class BillingSettings
    {
        public decimal UnitPrice { get; set; }
    }
}
