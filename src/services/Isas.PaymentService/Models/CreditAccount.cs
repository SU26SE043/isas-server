namespace PaymentService.Models
{
    /// <summary>
    /// Ví credit của 1 chủ sở hữu (Org hoặc User) — P1. 1 account / (owner_type, owner_id) — UNIQUE.
    /// Reserve/Consume/Release (P4/P5/P6) và Postpaid (P8a/P8b) SẼ thao tác trên bảng này ở các task sau;
    /// P1 chỉ tạo schema + khả năng tạo account rỗng (remaining=0, reserved=0, Active/Prepaid).
    /// </summary>
    public class CreditAccount
    {
        public Guid Id { get; set; }
        public OwnerType OwnerType { get; set; }
        public Guid OwnerId { get; set; }
        public PaymentMode PaymentMode { get; set; } = PaymentMode.Prepaid;
        public CreditAccountStatus Status { get; set; } = CreditAccountStatus.Active;
        public int RemainingCredits { get; set; }
        public int ReservedCredits { get; set; }
        public int? CreditLimit { get; set; }
        public int? PeriodUsage { get; set; }
        public DateTime UpdatedAt { get; set; }
    }

    public enum PaymentMode
    {
        Prepaid,
        Postpaid
    }

    public enum CreditAccountStatus
    {
        Active,
        Suspended
    }
}
