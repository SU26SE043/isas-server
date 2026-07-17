using Isas.Shared.Pagination;
using PaymentService.Models;
using static Isas.PaymentService.DTOs.OrderRequest;

namespace Isas.PaymentService.Services
{
    public interface IOrderService
    {
        Task<OrderResponse> CreateOrderAsync(OwnerType ownerType, Guid ownerId, CreateOrderRequest request, CancellationToken ct = default);

        /// <summary>
        /// P8b — tạo đơn tất toán hóa đơn postpaid (<c>kind=InvoiceSettlement</c>, gắn <c>invoice_id</c>,
        /// owner + amount lấy từ hóa đơn) rồi tạo link PayOS (REUSE cùng đường tạo order/PayOS với CreditPack).
        /// Trả <see cref="OrderResponse"/> kèm <c>CheckoutUrl</c>. Cộng "tất toán" chỉ xảy ra khi webhook Paid
        /// (WebhookService branch theo Kind), KHÔNG cộng credit.
        /// </summary>
        Task<OrderResponse> CreateInvoiceSettlementOrderAsync(Invoice invoice, CancellationToken ct = default);
        Task<OrderResponse?> GetOrderAsync(Guid id, CancellationToken ct = default);
        Task<List<OrderResponse>> GetOwnerOrdersAsync(OwnerType ownerType, Guid ownerId, CancellationToken ct = default);

        /// <summary>
        /// AUTH-7 — PlatformAdmin oversight: MỌI đơn xuyên chủ ví (KHÔNG lọc owner), read-only.
        /// Optional lọc status/ownerType. Cap 500, mới nhất trước.
        /// </summary>
        Task<KeysetPage<OrderResponse>> ListAllOrdersAsync(OrderStatus? status, OwnerType? ownerType, string? cursor, int? limit, CancellationToken ct = default);
        Task CancelOrderAsync(Guid id, CancellationToken ct = default);
    }
}
