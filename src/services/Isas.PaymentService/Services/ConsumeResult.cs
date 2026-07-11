namespace Isas.PaymentService.Services
{
    /// <summary>Kết quả consume 1 credit cho session (P5 — khi SessionScored).</summary>
    public enum ConsumeOutcome
    {
        /// <summary>Reservation Reserved→Consumed + ghi ledger −1, giảm reserved (remaining giữ nguyên).</summary>
        Consumed,
        /// <summary>Chưa có reservation cho session (miss event reserve) → no-op, KHÔNG trừ oan (§State machine).</summary>
        NoReservation,
        /// <summary>Reservation đã Consumed/Released (absorbing, PAY-11) → no-op idempotent, KHÔNG trừ lần 2.</summary>
        AlreadyFinalized
    }

    /// <summary>
    /// Kết quả gọi <c>ConsumeAsync</c>. Consume là best-effort/idempotent → mọi outcome controller
    /// map <c>200</c> (kể cả no-op: tránh kẹt retry ở caller — §State machine payment.md).
    /// </summary>
    public sealed record ConsumeResult(ConsumeOutcome Outcome, Guid? ReservationId)
    {
        public static ConsumeResult Consumed(Guid reservationId) =>
            new(ConsumeOutcome.Consumed, reservationId);

        public static ConsumeResult NoReservation() =>
            new(ConsumeOutcome.NoReservation, null);

        public static ConsumeResult AlreadyFinalized(Guid reservationId) =>
            new(ConsumeOutcome.AlreadyFinalized, reservationId);
    }
}
