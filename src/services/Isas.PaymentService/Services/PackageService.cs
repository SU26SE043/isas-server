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
            ValidateSanity(request.Name, request.PriceVnd, request.InterviewCredits, request.DurationDays);

            var package = new ProductPackage
            {
                Id = Guid.NewGuid(),
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

        // payment.md:109 — endpoint Public, phục vụ gói "đang bán" → lọc IsActive như GET catalog.
        // Không tìm thấy (id lạ HOẶC gói đã ngừng bán) → null → controller trả 404, không lộ gói đã rút.
        public async Task<PackageResponse?> GetPackageAsync(Guid id, CancellationToken ct)
        {
            var package = await _db.ProductPackages
                .FirstOrDefaultAsync(p => p.Id == id && p.IsActive, ct);

            return package is null ? null : PackageResponse.ToResponse(package);
        }

        public async Task<PackageResponse?> UpdatePackageAsync(Guid id, UpdatePackageRequest request, CancellationToken ct)
        {
            var package = await _db.ProductPackages.FindAsync(id);
            if (package is null) return null;

            // Update là partial (mọi field nullable) → chỉ sanity-check field CÓ MẶT, khỏi bắt buộc trường vắng.
            ValidateSanity(request.Name, request.PriceVnd, request.InterviewCredits, request.DurationDays);

            // R9 — OrderService chỉ bán OneTime có credits > 0. Chặn cùng hợp đồng ngay lúc catalog
            // được sửa, thay vì để UI hiển thị một gói mà bấm mua luôn 400.
            if (package.Type == PackageType.OneTime && request.InterviewCredits is <= 0)
                throw new ArgumentException("InterviewCredits must be > 0 for one_time packages.");

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
            if (type == PackageType.OneTime && credits is not > 0)
                throw new ArgumentException("InterviewCredits must be > 0 for one_time packages.");
            if (type == PackageType.Subscription && days is null)
                throw new ArgumentException("DurationDays is required for subscription packages.");
            if (type is not (PackageType.OneTime or PackageType.Subscription))
                throw new ArgumentException($"Invalid package type: {type}");
        }

        // Sanity-check dùng chung cho Create + Update: chặn name rỗng / số âm lọt xuống DB. Field nullable
        // ⇒ chỉ kiểm khi CÓ MẶT (Create truyền name/price non-null; Update partial nên vắng field = giữ cũ).
        private static void ValidateSanity(string? name, long? priceVnd, int? credits, int? days)
        {
            if (name is not null && string.IsNullOrWhiteSpace(name))
                throw new ArgumentException("Package name is required.");
            if (priceVnd is not null && priceVnd < 0)
                throw new ArgumentException("PriceVnd must be >= 0.");
            if (credits is not null && credits < 0)
                throw new ArgumentException("InterviewCredits must be >= 0.");
            if (days is not null && days < 0)
                throw new ArgumentException("DurationDays must be >= 0.");
        }
    }
}
