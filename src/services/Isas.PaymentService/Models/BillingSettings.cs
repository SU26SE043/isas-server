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

        /// <summary>
        /// F7 — số credit dùng thử tặng khi TẠO ví của một User (B2C). Mặc định <c>3</c>;
        /// đặt <c>0</c> = kill-switch (không tặng, không ghi sổ, hành vi về đúng như trước F7).
        /// Chỉ áp cho <c>owner_type = User</c> — ví Org (B2B) không có suất dùng thử (BC-1).
        /// Đổi giá trị KHÔNG hồi tố: ví đã tạo giữ nguyên <c>free_credits_granted</c> của nó.
        /// </summary>
        public int FreeTrialCredits { get; set; } = 3;
    }
}
