namespace Isas.PaymentService.Services
{
    /// <summary>Kết quả release chỗ giữ của session (P6 — khi SessionAbandoned/lỗi hệ thống).</summary>
    public enum ReleaseOutcome
    {
        /// <summary>Reservation Reserved→Released + hoàn chỗ giữ (reserved−1, remaining+1). KHÔNG ghi ledger.</summary>
        Released,
        /// <summary>Chưa có reservation cho session (miss event reserve) → no-op, KHÔNG hoàn oan (§State machine).</summary>
        NoReservation,
        /// <summary>Reservation đã Consumed/Released (absorbing, PAY-11) → no-op idempotent, KHÔNG hoàn oan.</summary>
        AlreadyFinalized
    }

    /// <summary>
    /// Kết quả gọi <c>ReleaseAsync</c>. Release là best-effort/idempotent → mọi outcome controller
    /// map <c>200</c> (kể cả no-op: tránh kẹt retry ở caller — §State machine payment.md).
    /// </summary>
    public sealed record ReleaseResult(ReleaseOutcome Outcome, Guid? ReservationId)
    {
        public static ReleaseResult Released(Guid reservationId) =>
            new(ReleaseOutcome.Released, reservationId);

        public static ReleaseResult NoReservation() =>
            new(ReleaseOutcome.NoReservation, null);

        public static ReleaseResult AlreadyFinalized(Guid reservationId) =>
            new(ReleaseOutcome.AlreadyFinalized, reservationId);
    }
}
