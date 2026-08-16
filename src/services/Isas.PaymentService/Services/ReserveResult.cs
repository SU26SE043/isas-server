namespace Isas.PaymentService.Services
{
    /// <summary>Kết quả reserve 1 credit cho session (P4).</summary>
    public enum ReserveOutcome
    {
        /// <summary>Đã giữ chỗ mới (remaining−1, reserved+1).</summary>
        Reserved,
        /// <summary>Session đã có reservation trước đó — idempotent, KHÔNG giữ thêm (PAY-4).</summary>
        AlreadyReserved,
        /// <summary>Hết credit (prepaid) / chạm hạn mức (postpaid) / không có ví / bị đình chỉ → 402 (PAY-5), KHÔNG tạo reservation.</summary>
        Insufficient,
        /// <summary>Chủ ví chưa có ví</summary>
        NoWallet,
        /// <summary>Prepaid hết credit</summary>
        OutOfCredit,
        /// <summary>Postpaid chạm credit_limit</summary>
        LimitReached,
        /// <summary>Còn hoá đơn quá hạn, BK17</summary>
        InvoiceOverdue,
        /// <summary>Ví bị đình chỉ, PAY-12</summary>
        Suspended
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

        public static ReserveResult NoWallet() => 
            new(ReserveOutcome.NoWallet, null, 0);

        public static ReserveResult OutOfCredit() => 
            new(ReserveOutcome.OutOfCredit, null, 0);

        public static ReserveResult LimitReached() => 
            new(ReserveOutcome.LimitReached, null, 0);

        public static ReserveResult InvoiceOverdue() => 
            new(ReserveOutcome.InvoiceOverdue, null, 0);

        public static ReserveResult Suspended() => 
            new(ReserveOutcome.Suspended, null, 0);
    }
}
