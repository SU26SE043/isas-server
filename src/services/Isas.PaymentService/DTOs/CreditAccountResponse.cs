using PaymentService.Models;

namespace Isas.PaymentService.DTOs
{
    /// <summary>
    /// payment.md §CreditAccount (dòng 68) — số dư ví trả cho `GET /payment/me/account`.
    /// Enum serialize SỐ, nhất quán với mọi DTO Payment còn lại (OrderResponse/PackageResponse).
    /// KHÔNG trả `Id` của ví: client định danh ví bằng (ownerType, ownerId) — đúng alternate key DB9.
    /// </summary>
    public class CreditAccountResponse
    {
        public OwnerType OwnerType { get; set; }
        public Guid OwnerId { get; set; }
        public PaymentMode PaymentMode { get; set; }
        public CreditAccountStatus Status { get; set; }
        public int RemainingCredits { get; set; }
        public int ReservedCredits { get; set; }

        /// <summary>
        /// F7 — số credit dùng thử đã được tặng cho ví này (0 = chưa từng, mọi ví Org). Thêm MỚI, additive:
        /// credit tặng nằm chung <see cref="RemainingCredits"/> nên client cũ không phải sửa gì.
        /// </summary>
        public int FreeCreditsGranted { get; set; }

        /// <summary>
        /// Ví đã tồn tại trong DB hay chưa. <c>false</c> = chưa từng mua credit và chưa từng luyện buổi
        /// nào — ví sẽ được tạo lazy ở lần reserve hoặc lần webhook Paid đầu tiên.
        ///
        /// Cần field này vì "chưa có ví" và "ví đã tiêu hết" đều trả <c>remainingCredits = 0</c>, nhưng
        /// là hai màn hình khác hẳn nhau: một bên phải mời dùng thử, bên kia phải mời nạp. Client KHÔNG
        /// suy được điều đó từ <c>freeCreditsGranted == 0</c> — ví tạo trước F7 cũng bằng 0, và nếu
        /// kill-switch <c>Billing:FreeTrialCredits</c> bị đặt về 0 thì MỌI ví đều bằng 0.
        /// </summary>
        public bool WalletExists { get; set; }

        /// <summary>
        /// Số credit dùng thử chủ ví SẼ nhận khi ví ra đời; <c>0</c> khi ví đã tồn tại, khi chủ ví là
        /// Org (BC-1), hoặc khi kill-switch tắt. Trả con số thật thay vì để client ghi cứng "3" —
        /// đổi <c>Billing:FreeTrialCredits</c> là một biến env, còn sửa chữ trong app thì phải phát
        /// hành lại bản mới.
        /// </summary>
        public int PendingFreeCredits { get; set; }

        public int? CreditLimit { get; set; }
        public int? PeriodUsage { get; set; }
        public DateTime UpdatedAt { get; set; }

        public static CreditAccountResponse ToResponse(CreditAccount a) => new()
        {
            OwnerType = a.OwnerType,
            OwnerId = a.OwnerId,
            PaymentMode = a.PaymentMode,
            Status = a.Status,
            RemainingCredits = a.RemainingCredits,
            ReservedCredits = a.ReservedCredits,
            FreeCreditsGranted = a.FreeCreditsGranted,
            WalletExists = true,
            PendingFreeCredits = 0,   // ví đã tồn tại ⇒ suất dùng thử (nếu có) đã cấp xong từ lúc INSERT
            CreditLimit = a.CreditLimit,
            PeriodUsage = a.PeriodUsage,
            UpdatedAt = a.UpdatedAt,
        };

        /// <summary>
        /// Chủ ví chưa từng mua/được cấp credit thì CHƯA có row `credit_accounts` (ví được tạo lazy lúc
        /// webhook Paid cộng credit — P2). payment.md:120 chỉ liệt kê lỗi 401 ⇒ đã đăng nhập thì luôn 200:
        /// trả ví rỗng Prepaid/Active thay vì 404, để FE hiện "0 credit" thay vì màn hình lỗi.
        /// KHÔNG ghi DB (đọc thuần) — ví thật vẫn do luồng thanh toán tạo.
        /// </summary>
        /// <param name="pendingFreeCredits">
        /// Số credit dùng thử chủ ví SẼ nhận khi ví ra đời. Lấy từ
        /// <see cref="Models.BillingSettings.FreeTrialGrantFor"/> — cùng một luật với đường cấp thật,
        /// nên đổi cấu hình không làm hai bên lệch nhau.
        /// </param>
        public static CreditAccountResponse Empty(
            OwnerType ownerType, Guid ownerId, int pendingFreeCredits = 0) => new()
        {
            OwnerType = ownerType,
            OwnerId = ownerId,
            PaymentMode = PaymentMode.Prepaid,
            Status = CreditAccountStatus.Active,
            RemainingCredits = 0,
            // F7 — `FreeCreditsGranted` vẫn CỐ Ý là 0: nó nói về QUÁ KHỨ ("đã cấp bao nhiêu"), mà ví
            // chưa tồn tại thì chưa cấp gì cả. Lời hứa về TƯƠNG LAI nay tách hẳn sang
            // `PendingFreeCredits` — hai câu khác nhau, đừng gộp lại.
            //
            // Ghi chú cũ ở đây từ chối hứa trước vì sợ "cấu hình đổi thì lời hứa không giữ được".
            // Nỗi lo đó nay đã được xử ở gốc: con số lấy từ chính `BillingSettings.FreeTrialGrantFor`
            // mà đường cấp dùng, nên không có bản sao nào để lệch. Đổi lại, giữ im lặng cũng có giá của
            // nó — người mới nhìn thấy "0 credit" ở đúng bước đầu phễu B2C trong khi họ thực sự có suất
            // dùng thử, và đó là lý do endpoint này được dựng lên ngay từ đầu.
            FreeCreditsGranted = 0,
            WalletExists = false,
            PendingFreeCredits = pendingFreeCredits,
            ReservedCredits = 0,
            CreditLimit = null,
            PeriodUsage = null,
            UpdatedAt = DateTime.UtcNow,
        };
    }
}
