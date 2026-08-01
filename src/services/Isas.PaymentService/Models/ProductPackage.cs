namespace PaymentService.Models
{
    public class ProductPackage : IHasUpdatedAt
    {
        public Guid Id { get; set; }
        public string Name { get; set; } = null!;
        public PackageType Type { get; set; }
        public long PriceVnd { get; set; }   // DB3 — bigint: giá pack lớn / VND > ~2,1 tỷ không tràn int
        public int? InterviewCredits { get; set; }
        public int? DurationDays { get; set; }
        public Guid? PlanId { get; set; }
        public PlanAudience? Audience { get; set; }
        public bool IsActive { get; set; }
        public DateTime CreatedAt { get; set; }
        // DB14 — audit: đóng dấu mỗi lần package bị sửa (Update/soft-delete IsActive qua PackageService,
        // tracked → SaveChanges override stamp). C# init để insert SQLite không phụ thuộc DB now().
        public DateTime UpdatedAt { get; set; } = DateTime.UtcNow;

        public ICollection<Order> Orders { get; set; } = [];
        public Plan? Plan { get; set; }
    }

    public enum PackageType
    {
        OneTime = 1,
        Subscription = 2
    }
}
