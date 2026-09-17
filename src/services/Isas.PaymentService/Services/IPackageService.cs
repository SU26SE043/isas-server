using Isas.PaymentService.DTOs;
using PaymentService.Models;

namespace Isas.PaymentService.Services
{
    public interface IPackageService
    {
        Task<List<PackageResponse>> GetAllPackagesAsync(CancellationToken ct = default);
        Task<PackageResponse?> GetPackageAsync(Guid id, CancellationToken ct = default);
        /// <summary>
        /// Bản cho ADMIN: <paramref name="includeInactive"/> = true trả cả gói đã ẩn (soft-delete) — không có nó
        /// thì gói đã ẩn biến mất khỏi mọi list và không bật lại được từ UI (PUT <c>isActive:true</c> cần id).
        /// Controller chỉ cho phép khi caller có role Admin; public vẫn chỉ thấy gói đang bán.
        /// </summary>
        Task<List<PackageResponse>> GetAllPackagesAsync(bool includeInactive, CancellationToken ct = default);
        Task<PackageResponse?> GetPackageAsync(Guid id, bool includeInactive, CancellationToken ct = default);
        Task<PackageResponse> CreatePackageAsync(CreatePackageRequest request, CancellationToken ct = default);
        Task<PackageResponse?> UpdatePackageAsync(Guid id, UpdatePackageRequest request, CancellationToken ct = default);
        Task<bool> DeletePackageAsync(Guid id, CancellationToken ct = default);
    }
}
