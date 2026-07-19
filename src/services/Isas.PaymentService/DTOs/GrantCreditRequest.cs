using System.ComponentModel.DataAnnotations;
using Isas.PaymentService.Services;
using PaymentService.Models;

namespace Isas.PaymentService.DTOs
{
    /// <summary>F20 — thân request `POST /payment/admin/credits/grant`.</summary>
    public class GrantCreditRequest
    {
        /// <summary>Ví nhận: Org (B2B) hay User (B2C).</summary>
        [Required]
        public OwnerType? OwnerType { get; set; }

        [Required]
        public Guid? OwnerId { get; set; }

        /// <summary>
        /// Số credit cấp thêm. Phải &gt; 0 — muốn TRỪ credit thì đi đường hoàn tiền (F18), nơi có bút toán
        /// đảo gắn khoản gốc; "cấp số âm" sẽ là một đường trừ credit không dấu vết.
        /// </summary>
        [Range(1, 10_000)]
        public int Credits { get; set; }

        /// <summary>Lý do cấp (khuyến mãi / đền bù sự cố). Bắt buộc — quà không lý do thì không đối soát được.</summary>
        [Required]
        [StringLength(500, MinimumLength = 3)]
        public string Note { get; set; } = null!;
    }

    public class GrantCreditResponse
    {
        public OwnerType OwnerType { get; set; }
        public Guid OwnerId { get; set; }
        public int CreditsGranted { get; set; }
        public int RemainingCredits { get; set; }
        public Guid? TransactionId { get; set; }

        public static GrantCreditResponse From(GrantResult r) => new()
        {
            OwnerType = r.OwnerType,
            OwnerId = r.OwnerId,
            CreditsGranted = r.CreditsGranted,
            RemainingCredits = r.RemainingCredits,
            TransactionId = r.TransactionId,
        };
    }
}
