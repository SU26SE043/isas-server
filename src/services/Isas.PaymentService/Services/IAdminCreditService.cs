using PaymentService.Models;

namespace Isas.PaymentService.Services
{
    /// <summary>Kết cục của một lệnh cấp credit khuyến mãi.</summary>
    public enum GrantOutcome
    {
        Granted,

        /// <summary>Số credit ≤ 0 — không có gì để cấp (và ledger delta = 0 sẽ nổ CHECK).</summary>
        InvalidAmount,

        /// <summary>
        /// Ví vừa biến mất giữa chừng (thực tế không xảy ra — ví không bao giờ bị xoá). Giữ nhánh để
        /// KHÔNG âm thầm báo thành công khi câu cộng số dư khớp 0 row.
        /// </summary>
        WalletMissing
    }

    public sealed record GrantResult(
        GrantOutcome Outcome,
        OwnerType OwnerType,
        Guid OwnerId,
        int CreditsGranted,
        int RemainingCredits,
        Guid? TransactionId);

    /// <summary>
    /// F20 (vế Payment) — PlatformAdmin cấp credit khuyến mãi cho ví một chủ sở hữu, và đọc được ví của
    /// người khác. Hai vế Auth (cấm tài khoản · đặt lại mật khẩu hộ) đã làm ở vòng trước.
    ///
    /// ⚠ Vì sao phải nằm ở ĐÂY chứ không phải một service admin riêng: AUTH-7 — endpoint admin nằm trong
    /// service SỞ HỮU dữ liệu. Và trước F20 không hề có đường nào để admin đọc/sửa ví người khác:
    /// <c>me/account</c> suy chủ ví từ JWT nên nó chỉ bao giờ nói về chính người gọi.
    /// </summary>
    public interface IAdminCreditService
    {
        Task<GrantResult> GrantAsync(
            OwnerType ownerType, Guid ownerId, int credits, string? note, Guid adminUserId,
            CancellationToken ct = default);
    }
}
