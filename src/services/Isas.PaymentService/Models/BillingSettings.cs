// `global::` là BẮT BUỘC, không phải trang trí: file này nằm trong `Isas.PaymentService.Models`, nên
// `using PaymentService.Models;` trần sẽ được phân giải thành `Isas.PaymentService.Models` (namespace
// bao ngoài `Isas` có chứa `PaymentService`) — tức trỏ vào chính nó và không thấy `OwnerType`.
using global::PaymentService.Models;

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

        /// <summary>
        /// Số credit dùng thử một chủ ví SẼ được tặng khi ví của họ ra đời — <c>0</c> nếu là ví Org
        /// hoặc kill-switch đang tắt. Đặt ở đây (không phải trong <c>CreditAccountService</c>) vì có
        /// HAI nơi cần cùng một luật: đường CẤP lúc tạo ví, và đường ĐỌC <c>GET /me/account</c> báo cho
        /// người chưa có ví biết họ sắp được tặng bao nhiêu. Hai bản sao của luật này sẽ lệch nhau
        /// trong im lặng — lệch nghĩa là hứa sai với người dùng về số lượt miễn phí.
        /// </summary>
        public int FreeTrialGrantFor(OwnerType ownerType) =>
            ownerType == OwnerType.User && FreeTrialCredits > 0 ? FreeTrialCredits : 0;

        /// <summary>F23/BK24 — số ngày từ periodEnd tới hạn tất toán hóa đơn postpaid. Snapshot vào
        /// `Invoice.DueAt` lúc lập; đổi giá trị này KHÔNG hồi tố hóa đơn đã có DueAt.</summary>
        public int InvoiceDueDays { get; set; } = 15;
    }
}
