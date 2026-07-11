using Isas.PaymentService.DTOs;
using PaymentService.Models;

namespace Isas.PaymentService.Services
{
    public interface IPackageService
    {
        Task<List<PackageResponse>> GetAllPackagesAsync(CancellationToken ct = default);
        Task<PackageResponse?> GetPackageAsync(Guid id, CancellationToken ct = default);
        Task<PackageResponse> CreatePackageAsync(CreatePackageRequest request, CancellationToken ct = default);
        Task<PackageResponse?> UpdatePackageAsync(Guid id, UpdatePackageRequest request, CancellationToken ct = default);
        Task<bool> DeletePackageAsync(Guid id, CancellationToken ct = default);
    }
}
