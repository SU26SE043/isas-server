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

    /// <summary>F23/BK24 — kết cục của lệnh duyệt/đổi payment mode.</summary>
    public enum SetPaymentModeOutcome
    {
        Updated,

        /// <summary>OwnerType=User — payment.md "User LUÔN Prepaid" (D15). Chỉ Org đổi được mode.</summary>
        NotOrg,

        /// <summary>Postpaid mà creditLimit null/≤0, HOẶC Prepaid mà creditLimit có giá trị.</summary>
        InvalidCreditLimit,

        /// <summary>Chưa có ví — KHÔNG tạo ví lazy ở đây (tạo ví = tạo đối tượng tiền, ngoài phạm vi duyệt mode).</summary>
        WalletMissing,

        /// <summary>Prepaid→Postpaid khi remaining/reserved &gt; 0 và chưa opt-in `AllowStrandedCredits`.</summary>
        StrandedCredits,

        /// <summary>Postpaid→Prepaid khi còn invoice Issued/Overdue hoặc period_usage &gt; 0 chưa chốt kỳ.</summary>
        UnpaidDebt,

        /// <summary>CAS 0 row — mode đã bị đổi bởi thao tác khác xen giữa lúc đọc và lúc ghi.</summary>
        Conflict
    }

    public sealed record SetPaymentModeResult(
        SetPaymentModeOutcome Outcome,
        OwnerType OwnerType,
        Guid OwnerId,
        PaymentMode PaymentMode,
        int? CreditLimit,
        int RemainingCredits,
        int ReservedCredits);

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
            OwnerType ownerType, Guid ownerId, int credits, string? note, string? idempotencyKey, Guid adminUserId,
            CancellationToken ct = default);

        /// <summary>
        /// F23/BK24 — PlatformAdmin duyệt/đổi payment mode của MỘT ví Org. Xem BK24 plan §3 cho outcome
        /// table đầy đủ. Người duyệt (<paramref name="adminUserId"/>) lấy từ JWT ở controller, KHÔNG
        /// nhận từ body (cùng lý lẽ granted_by/refunded_by).
        /// </summary>
        Task<SetPaymentModeResult> SetPaymentModeAsync(
            OwnerType ownerType, Guid ownerId, PaymentMode paymentMode, int? creditLimit,
            string note, bool allowStrandedCredits, Guid adminUserId,
            CancellationToken ct = default);
    }
}
