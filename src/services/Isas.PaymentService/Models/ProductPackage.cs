namespace PaymentService.Models
{
    public class ProductPackage
    {
        public Guid Id { get; set; }
        public string Name { get; set; } = null!;
        public PackageType Type { get; set; }
        public long PriceVnd { get; set; }   // DB3 — bigint: giá pack lớn / VND > ~2,1 tỷ không tràn int
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
