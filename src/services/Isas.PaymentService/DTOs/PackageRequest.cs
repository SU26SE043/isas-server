using PaymentService.Models;

namespace Isas.PaymentService.DTOs
{
    public class CreatePackageRequest
    {
        public string Name { get; set; } = null!;
        public PackageType Type { get; set; } // "one_time" | "subscription"
        public long PriceVnd { get; set; }   // DB3 — bigint khớp ProductPackage.PriceVnd
        public int? InterviewCredits { get; set; } // required if Type == "one_time"
        public int? DurationDays { get; set; }     // required if Type == "subscription"
    }

    public class UpdatePackageRequest
    {
        public string? Name { get; set; }
        public long? PriceVnd { get; set; }
        public int? InterviewCredits { get; set; }
        public int? DurationDays { get; set; }
        public bool? IsActive { get; set; }
    }

    public class PackageResponse
    {
        public Guid Id { get; set; }
        public string Name { get; set; } = null!;
        public PackageType Type { get; set; }
        public long PriceVnd { get; set; }
        public int? InterviewCredits { get; set; }
        public int? DurationDays { get; set; }
        public bool IsActive { get; set; }
        public DateTime CreatedAt { get; set; }
        public static PackageResponse ToResponse(ProductPackage p) => new PackageResponse()
        {
            Id = p.Id,
            Name = p.Name,
            Type = p.Type,
            PriceVnd = p.PriceVnd,
            InterviewCredits = p.InterviewCredits,
            DurationDays = p.DurationDays,
            IsActive = p.IsActive,
            CreatedAt = p.CreatedAt
        };
    }
}
