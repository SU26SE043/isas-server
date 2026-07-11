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

        /// <summary>
        /// P5 — trừ thật 1 credit khi session <paramref name="sessionId"/> được chấm (Consume trong D7,
        /// event SessionScored). Reservation <c>Reserved→Consumed</c> (atomic guard WHERE status=Reserved)
        /// + <c>reserved−1</c> + ghi <c>credit_transactions(Consume, −1)</c> — <c>remaining</c> giữ nguyên.
        /// Idempotent/absorbing theo <paramref name="sessionId"/> (PAY-11): reservation đã Consumed/Released
        /// → <see cref="ConsumeOutcome.AlreadyFinalized"/> no-op; chưa có reservation (miss reserve) →
        /// <see cref="ConsumeOutcome.NoReservation"/> no-op — cả hai KHÔNG tạo bút toán/trừ oan.
        /// Chủ ví lấy từ reservation (nguồn chân lý), không tin owner trong request.
        /// </summary>
        Task<ConsumeResult> ConsumeAsync(Guid sessionId, CancellationToken ct = default);

        /// <summary>
        /// P6 — nhả chỗ giữ khi session <paramref name="sessionId"/> bỏ ngang/lỗi hệ thống (Release trong
        /// D7, event SessionAbandoned). Reservation <c>Reserved→Released</c> (atomic guard WHERE status=Reserved)
        /// + hoàn chỗ giữ <c>reserved−1, remaining+1</c> — <b>KHÔNG</b> ghi <c>credit_transactions</c>
        /// (credit đã giữ được trả lại, không tiêu; bảo toàn bất biến audit). Idempotent/absorbing theo
        /// <paramref name="sessionId"/> (PAY-11): reservation đã Consumed/Released →
        /// <see cref="ReleaseOutcome.AlreadyFinalized"/> no-op (KHÔNG hoàn oan sau khi đã tiêu); chưa có
        /// reservation (miss reserve) → <see cref="ReleaseOutcome.NoReservation"/> no-op. Chủ ví lấy từ
        /// reservation (nguồn chân lý), không tin owner trong request.
        /// </summary>
        Task<ReleaseResult> ReleaseAsync(Guid sessionId, CancellationToken ct = default);
    }
}
