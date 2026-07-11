namespace PaymentService.Models
{
    public class ProductPackage
    {
        public Guid Id { get; set; }
        public string Name { get; set; } = null!;
        public PackageType Type { get; set; }
        public int PriceVnd { get; set; }
        public int? InterviewCredits { get; set; }
        public int? DurationDays { get; set; }
        public bool IsActive { get; set; }
        public DateTime CreatedAt { get; set; }

        public ICollection<Order> Orders { get; set; } = [];
        public ICollection<Subscription> Subscriptions { get; set; } = [];
    }

    public enum PackageType
    {
        OneTime = 1,
        Subscription = 2
    }
}
