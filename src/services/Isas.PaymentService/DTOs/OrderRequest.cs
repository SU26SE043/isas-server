using PaymentService.Models;

namespace Isas.PaymentService.DTOs
{
    public class OrderRequest
    {
        public class CreateOrderRequest
        {
            public Guid PackageId { get; set; }
        }

        // P3 — active-polling đối soát (payment.md:145). Trả trạng thái sau đối soát PayOS.
        public class OrderStatusResponse
        {
            public long OrderCode { get; set; }
            public string Status { get; set; } = null!;   // enum string: Pending·Paid·Failed·Expired·Cancelled
            public DateTime? PaidAt { get; set; }
        }

        public class OrderResponse
        {
            public Guid Id { get; set; }
            public OwnerType OwnerType { get; set; }
            public Guid OwnerId { get; set; }
            public OrderKind Kind { get; set; }
            public Guid? PackageId { get; set; }
            public Guid? InvoiceId { get; set; }
            public OrderStatus Status { get; set; }
            public int AmountVnd { get; set; }
            public long PayosOrderCode { get; set; }
            public DateTime ExpiredAt { get; set; }
            public DateTime? PaidAt { get; set; }
            public DateTime CreatedAt { get; set; }
            public string? CheckoutUrl { get; set; }
            public static OrderResponse ToResponse(Order order) => new OrderResponse
            {
                Id = order.Id,
                OwnerType = order.OwnerType,
                OwnerId = order.OwnerId,
                Kind = order.Kind,
                PackageId = order.PackageId,
                InvoiceId = order.InvoiceId,
                Status = order.Status,
                AmountVnd = order.AmountVnd,
                PayosOrderCode = order.PayosOrderCode,
                ExpiredAt = order.ExpiredAt,
                PaidAt = order.PaidAt,
                CreatedAt = order.CreatedAt
            };
        }
    }
}
