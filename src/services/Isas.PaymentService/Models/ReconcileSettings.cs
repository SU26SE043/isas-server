namespace PaymentService.Models
{
    /// <summary>
    /// DB4 — cấu hình reconciler bất biến <c>credit_accounts.reserved_credits ==
    /// count(credit_reservations status=Reserved)</c>. Bind section <c>Reconcile</c>.
    /// <see cref="Enabled"/>=false → tắt hẳn (safe-disable cho môi trường không muốn chạy nền).
    /// <see cref="ScanIntervalSeconds"/> = chu kỳ quét (giây); ≤0 rơi về mặc định 120s.
    /// Đây là config thuần (không cột DB) → KHÔNG migration.
    /// </summary>
    public class ReconcileSettings
    {
        public bool Enabled { get; set; } = true;
        public int ScanIntervalSeconds { get; set; } = 120;
    }
}
