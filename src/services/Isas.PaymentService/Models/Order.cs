namespace PaymentService.Models
{
    public class Order
    {
        public Guid Id { get; set; }
        public Guid UserId { get; set; }
        public Guid PackageId { get; set; }
        public OrderStatus Status { get; set; }
        public int AmountVnd { get; set; }
        public long PayosOrderCode { get; set; }
        public DateTime ExpiredAt { get; set; }
        public DateTime? PaidAt { get; set; }
        public DateTime CreatedAt { get; set; }
        public ProductPackage Package { get; set; } = null!;
        public PaymentTransaction? PaymentTransaction { get; set; }
        public Subscription? Subscription { get; set; }
        public ICollection<CreditTransaction> CreditTransactions { get; set; } = [];
    }
    public enum OrderStatus
    {
        Pending = 1,
        Paid = 2,
        Failed = 3,
        Expired = 4,
        Cancelled = 5,
    }
}
