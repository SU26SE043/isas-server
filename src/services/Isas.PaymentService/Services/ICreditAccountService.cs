using PaymentService.Models;

namespace Isas.PaymentService.Services
{
    /// <summary>
    /// P1 — cấp phát ví credit theo chủ sở hữu (Org/User). P4 mở rộng thêm Reserve (giữ chỗ);
    /// Consume/Release (P5/P6) và Postpaid (P8a/P8b) sẽ bổ sung sau.
    /// </summary>
    public interface ICreditAccountService
    {
        /// <summary>Tạo credit_account mới (Prepaid/Active, 0 credit). Ném nếu (ownerType, ownerId) đã có ví.</summary>
        Task<CreditAccount> CreateAccountAsync(OwnerType ownerType, Guid ownerId, CancellationToken ct = default);

        Task<CreditAccount?> GetAccountAsync(OwnerType ownerType, Guid ownerId, CancellationToken ct = default);

        /// <summary>
        /// P4 — giữ chỗ 1 credit cho <paramref name="sessionId"/> (Reserve trong D7). Prepaid:
        /// atomic <c>remaining−1, reserved+1 WHERE remaining≥1 AND status=Active</c> (chống double-spend,
        /// PAY-5). Idempotent theo <paramref name="sessionId"/> (PAY-4): gọi lại cùng session KHÔNG giữ
        /// thêm. Hết credit / không có ví / bị đình chỉ → <see cref="ReserveOutcome.Insufficient"/>
        /// (controller → 402), KHÔNG tạo reservation dư.
        /// </summary>
        Task<ReserveResult> ReserveAsync(OwnerType ownerType, Guid ownerId, Guid sessionId, CancellationToken ct = default);
    }
}
