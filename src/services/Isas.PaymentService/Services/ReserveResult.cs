namespace Isas.PaymentService.Services
{
    /// <summary>Kết quả reserve 1 credit cho session (P4).</summary>
    public enum ReserveOutcome
    {
        /// <summary>Đã giữ chỗ mới (remaining−1, reserved+1).</summary>
        Reserved,
        /// <summary>Session đã có reservation trước đó — idempotent, KHÔNG giữ thêm (PAY-4).</summary>
        AlreadyReserved,
        /// <summary>Hết credit / không có ví / bị đình chỉ → 402 (PAY-5), KHÔNG tạo reservation.</summary>
        Insufficient
    }

    /// <summary>
    /// Kết quả gọi <c>ReserveAsync</c>. Controller map: Insufficient → 402; còn lại → 200
    /// (<c>{ reservationId, reservedCredits }</c>).
    /// </summary>
    public sealed record ReserveResult(ReserveOutcome Outcome, Guid? ReservationId, int ReservedCredits)
    {
        public static ReserveResult Reserved(Guid reservationId, int reservedCredits) =>
            new(ReserveOutcome.Reserved, reservationId, reservedCredits);

        public static ReserveResult AlreadyReserved(Guid reservationId, int reservedCredits) =>
            new(ReserveOutcome.AlreadyReserved, reservationId, reservedCredits);

        public static ReserveResult Insufficient() =>
            new(ReserveOutcome.Insufficient, null, 0);
    }
}
