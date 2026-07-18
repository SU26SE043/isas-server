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
        public static CreditAccountResponse Empty(OwnerType ownerType, Guid ownerId) => new()
        {
            OwnerType = ownerType,
            OwnerId = ownerId,
            PaymentMode = PaymentMode.Prepaid,
            Status = CreditAccountStatus.Active,
            RemainingCredits = 0,
            ReservedCredits = 0,
            CreditLimit = null,
            PeriodUsage = null,
            UpdatedAt = DateTime.UtcNow,
        };
    }
}
