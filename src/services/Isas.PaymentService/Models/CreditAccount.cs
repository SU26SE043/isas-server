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

        /// <summary>
        /// F7 — số credit dùng thử đã TẶNG cho ví này lúc tạo (0 = chưa từng tặng, mọi ví Org).
        /// Denormalize từ sổ cái (`credit_transactions` Reason=FreeGrant) để trả lời "user này đã dùng
        /// suất dùng thử chưa" bằng 1 row read, và để luồng hoàn tiền sau này (F18) biết phần nào là
        /// tiền khách trả, phần nào được tặng. KHÔNG phải một xô riêng: credit tặng nằm chung
        /// `remaining_credits` và tiêu theo đúng luật hiện hành (PAY-4/PAY-11/PAY-13).
        /// </summary>
        public int FreeCreditsGranted { get; set; }
        public int? CreditLimit { get; set; }
        public int? PeriodUsage { get; set; }

        /// <summary>
        /// F23/BK24 — vết lần đổi <see cref="PaymentMode"/> GẦN NHẤT (không phải lịch sử đầy đủ,
        /// chỉ đủ trả lời "ai duyệt, lúc nào, vì sao" cho lần hiện tại). Null = ví chưa từng bị đổi
        /// mode qua endpoint duyệt (vẫn Prepaid mặc định từ lúc tạo).
        /// </summary>
        public DateTime? PaymentModeChangedAt { get; set; }
        public Guid? PaymentModeChangedBy { get; set; }
        public string? PaymentModeChangedNote { get; set; }

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
