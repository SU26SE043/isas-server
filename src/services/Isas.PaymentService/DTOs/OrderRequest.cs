using PaymentService.Models;

namespace Isas.PaymentService.DTOs
{
    public class OrderRequest
    {
        public class CreateOrderRequest
        {
            public Guid PackageId { get; set; }
        }

        public class OrderResponse
        {
            public Guid Id { get; set; }
            public Guid UserId { get; set; }
            public Guid PackageId { get; set; }
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
                UserId = order.UserId,
                PackageId = order.PackageId,
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
