using Isas.PaymentService.Services;
using PaymentService.Models;
using System.ComponentModel.DataAnnotations;

namespace Isas.PaymentService.DTOs
{
    /// <summary>F23/BK24 — thân request `POST /payment/admin/credits/payment-mode`.</summary>
    public class SetPaymentModeRequest
    {
        [Required]
        public OwnerType? OwnerType { get; set; }

        [Required]
        public Guid? OwnerId { get; set; }

        [Required]
        public PaymentMode? PaymentMode { get; set; }

        /// <summary>
        /// BẮT BUỘC khi PaymentMode=Postpaid (>0, ép ở [Range]); PHẢI để trống khi PaymentMode=Prepaid —
        /// validate combo đầy đủ nằm ở <see cref="AdminCreditService.SetPaymentModeAsync"/> vì phụ thuộc
        /// giá trị của field khác (data annotation không tự so sánh chéo field được sạch sẽ).
        /// </summary>
        [Range(1, 100000)]
        public int? CreditLimit { get; set; }

        /// <summary>Lý do duyệt/đổi mode (bắt buộc — cùng lý lẽ Reason của RefundOrderRequest).</summary>
        [Required]
        [StringLength(500, MinimumLength = 3)]
        public string Note { get; set; } = null!;

        /// <summary>
        /// Opt-in tường minh cho Prepaid→Postpaid khi ví còn remaining/reserved &gt; 0 (credit sẽ bị mắc
        /// kẹt, không tiêu được ở Postpaid). Mẫu <c>RefundOrderRequest.AllowPartialClawback</c>.
        /// </summary>
        public bool AllowStrandedCredits { get; set; }
    }

    /// <summary>F23/BK24 — phản hồi duyệt/đổi payment mode.</summary>
    public class SetPaymentModeResponse
    {
        public OwnerType OwnerType { get; set; }
        public Guid OwnerId { get; set; }
        public PaymentMode PaymentMode { get; set; }
        public int? CreditLimit { get; set; }
        public int RemainingCredits { get; set; }
        public int ReservedCredits { get; set; }

        public static SetPaymentModeResponse From(SetPaymentModeResult r) => new()
        {
            OwnerType = r.OwnerType,
            OwnerId = r.OwnerId,
            PaymentMode = r.PaymentMode,
            CreditLimit = r.CreditLimit,
            RemainingCredits = r.RemainingCredits,
            ReservedCredits = r.ReservedCredits,
        };
    }
}
