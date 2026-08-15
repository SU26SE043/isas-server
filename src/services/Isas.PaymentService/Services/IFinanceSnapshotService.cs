using Isas.PaymentService.DTOs;

namespace Isas.PaymentService.Services
{
    public interface IFinanceSnapshotService
    {
        Task<FinanceSnapshotResponse> GetSnapshotAsync(CancellationToken ct = default);
    }
}
