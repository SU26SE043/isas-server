using PaymentService.Models;
using static Isas.PaymentService.DTOs.OrderRequest;

namespace Isas.PaymentService.Services
{
    public interface IOrderService
    {
        Task<OrderResponse> CreateOrderAsync(Guid userId, CreateOrderRequest request, CancellationToken ct = default);
        Task<OrderResponse?> GetOrderAsync(Guid id, CancellationToken ct = default);
        Task<List<OrderResponse>> GetUserOrdersAsync(Guid userId, CancellationToken ct = default);
        Task CancelOrderAsync(Guid id, CancellationToken ct = default);
    }
}
