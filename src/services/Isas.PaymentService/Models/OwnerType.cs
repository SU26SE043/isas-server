namespace PaymentService.Models
{
    /// <summary>
    /// Chủ ví credit — Org (B2B) hoặc User (B2C cá nhân, prepaid-only). Dùng chung cho
    /// credit_accounts / credit_reservations / credit_transactions (D15). Lưu string trong DB (GEN-2).
    /// </summary>
    public enum OwnerType
    {
        Org,
        User
    }
}
