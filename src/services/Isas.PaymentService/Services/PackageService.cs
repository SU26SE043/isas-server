using Isas.PaymentService.DTOs;
using Microsoft.EntityFrameworkCore;
using PaymentService.Models;

namespace Isas.PaymentService.Services
{
    public class PackageService : IPackageService
    {
        private readonly ILogger<PackageService> _logger;
        private readonly PaymentDbContext _db;
        
        public PackageService(ILogger<PackageService> logger, PaymentDbContext db)
        {
            _logger = logger;
            _db = db;
        }

        public async Task<PackageResponse> CreatePackageAsync(CreatePackageRequest request, CancellationToken ct)
        {
            Validate(request.Type, request.InterviewCredits, request.DurationDays);

            var package = new ProductPackage
            {
                Name = request.Name,
                Type = request.Type,
                PriceVnd = request.PriceVnd,
                InterviewCredits = request.InterviewCredits,
                DurationDays = request.DurationDays,
                IsActive = true,
                CreatedAt = DateTime.UtcNow
            };

            _db.ProductPackages.Add(package);
            await _db.SaveChangesAsync();
            return PackageResponse.ToResponse(package);
        }

        public async Task<bool> DeletePackageAsync(Guid id, CancellationToken ct)
        {
            var package = await _db.ProductPackages.FindAsync(id);
            if (package is null) return false;

            package.IsActive = false;
            await _db.SaveChangesAsync();
            return true;
        }

        public async Task<List<PackageResponse>> GetAllPackagesAsync(CancellationToken ct)
        {
            return await _db.ProductPackages
                .Where(p => p.IsActive)
                .OrderBy(p => p.CreatedAt)
                .Select(p => PackageResponse.ToResponse(p))
                .ToListAsync(ct);
        }

        public async Task<PackageResponse?> GetPackageAsync(Guid id, CancellationToken ct)
        {
            var package = await _db.ProductPackages.FirstOrDefaultAsync(p => p.Id == id, ct);

            return PackageResponse.ToResponse(package);
        }

        public async Task<PackageResponse?> UpdatePackageAsync(Guid id, UpdatePackageRequest request, CancellationToken ct)
        {
            var package = await _db.ProductPackages.FindAsync(id);
            if (package is null) return null;

            if (request.Name is not null) package.Name = request.Name;
            if (request.PriceVnd is not null) package.PriceVnd = request.PriceVnd.Value;
            if (request.InterviewCredits is not null) package.InterviewCredits = request.InterviewCredits;
            if (request.DurationDays is not null) package.DurationDays = request.DurationDays;
            if (request.IsActive is not null) package.IsActive = request.IsActive.Value;

            await _db.SaveChangesAsync();
            return PackageResponse.ToResponse(package);
        }

        private static void Validate(PackageType type, int? credits, int? days)
        {
            if (type == PackageType.OneTime && credits is null)
                throw new ArgumentException("InterviewCredits is required for one_time packages.");
            if (type == PackageType.Subscription && days is null)
                throw new ArgumentException("DurationDays is required for subscription packages.");
            if (type is not (PackageType.OneTime or PackageType.Subscription))
                throw new ArgumentException($"Invalid package type: {type}");
        }
    }
}
