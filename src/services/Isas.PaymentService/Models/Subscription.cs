namespace PaymentService.Models
{
    public class Subscription
    {
        public Guid Id { get; set; }
        public Guid UserId { get; set; }
        public Guid OrderId { get; set; }
        public Guid PackageId { get; set; }
        public string Status { get; set; } = "active"; // active | expired | cancelled
        public DateTime StartedAt { get; set; }
        public DateTime ExpiresAt { get; set; }
        public DateTime CreatedAt { get; set; }
        public Order Order { get; set; } = null!;
        public ProductPackage Package { get; set; } = null!;
    }
}
