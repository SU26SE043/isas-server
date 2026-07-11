using PaymentService.Models;

namespace Isas.PaymentService.Services
{
    /// <summary>
    /// P1 — cấp phát ví credit theo chủ sở hữu (Org/User). KHÔNG chứa logic Reserve/Consume/Release
    /// (P4/P5/P6) hay Postpaid (P8a/P8b) — những task đó sẽ mở rộng service này sau.
    /// </summary>
    public interface ICreditAccountService
    {
        /// <summary>Tạo credit_account mới (Prepaid/Active, 0 credit). Ném nếu (ownerType, ownerId) đã có ví.</summary>
        Task<CreditAccount> CreateAccountAsync(OwnerType ownerType, Guid ownerId, CancellationToken ct = default);

        Task<CreditAccount?> GetAccountAsync(OwnerType ownerType, Guid ownerId, CancellationToken ct = default);
    }
}
