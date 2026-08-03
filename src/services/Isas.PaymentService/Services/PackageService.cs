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
            await ValidateAsync(request.Type, request.InterviewCredits, request.DurationDays,
                request.PlanId, request.Audience, ct);
            ValidateSanity(request.Name, request.PriceVnd, request.InterviewCredits, request.DurationDays);

            var package = new ProductPackage
            {
                Id = Guid.NewGuid(),
                Name = request.Name,
                Type = request.Type,
                PriceVnd = request.PriceVnd,
                InterviewCredits = request.InterviewCredits,
                DurationDays = request.DurationDays,
                PlanId = request.PlanId,
                Audience = request.Audience,
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

            var nextPlanId = request.PlanId ?? package.PlanId;
            var nextAudience = request.Audience ?? package.Audience;
            var changesSubscriptionBinding = package.Type == PackageType.Subscription &&
                (nextPlanId != package.PlanId || nextAudience != package.Audience);
            if (changesSubscriptionBinding)
            {
                var hasPendingOrder = await _db.Orders.AnyAsync(o => o.PackageId == package.Id && o.Status == OrderStatus.Pending, ct);
                var hasActiveSubscription = await _db.Subscriptions.AnyAsync(s => s.PackageId == package.Id && s.Status == SubscriptionStatus.Active && s.ExpiresAt > DateTime.UtcNow, ct);
                if (hasPendingOrder || hasActiveSubscription)
                    throw new ArgumentException("Cannot change a subscription package plan or audience while pending orders or active subscriptions exist.");
            }
            if (package.Type == PackageType.Subscription)
                await ValidateAsync(package.Type, package.InterviewCredits, request.DurationDays ?? package.DurationDays,
                    nextPlanId, nextAudience, ct);
            else if (request.PlanId is not null || request.Audience is not null)
                throw new ArgumentException("OneTime packages cannot reference a plan or audience.");

            if (request.Name is not null) package.Name = request.Name;
            if (request.PriceVnd is not null) package.PriceVnd = request.PriceVnd.Value;
            if (request.InterviewCredits is not null) package.InterviewCredits = request.InterviewCredits;
            if (request.DurationDays is not null) package.DurationDays = request.DurationDays;
            if (request.IsActive is not null) package.IsActive = request.IsActive.Value;
            if (request.PlanId is not null) package.PlanId = request.PlanId;
            if (request.Audience is not null) package.Audience = request.Audience;

            await _db.SaveChangesAsync();
            return PackageResponse.ToResponse(package);
        }

        private async Task ValidateAsync(PackageType type, int? credits, int? days,
            Guid? planId, PlanAudience? audience, CancellationToken ct)
        {
            if (type == PackageType.OneTime && credits is not > 0)
                throw new ArgumentException("InterviewCredits must be > 0 for one_time packages.");
            if (type == PackageType.Subscription)
            {
                if (days is not > 0) throw new ArgumentException("DurationDays is required for subscription packages.");
                if (planId is null || audience is null)
                    throw new ArgumentException("Subscription packages require PlanId and Audience.");
                var plan = await _db.Plans.AsNoTracking()
                    .SingleOrDefaultAsync(p => p.Id == planId && p.IsActive, ct);
                if (plan is null) throw new ArgumentException("Plan not found or inactive.");
                if (plan.Audience != audience)
                    throw new ArgumentException("Subscription package audience must match its plan.");
            }
            else if (planId is not null || audience is not null)
                throw new ArgumentException("OneTime packages cannot reference a plan or audience.");
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
