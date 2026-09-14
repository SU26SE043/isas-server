using PaymentService.Models;

namespace Isas.PaymentService.DTOs
{
    public class OrderRequest
    {
        public class CreateOrderRequest
        {
            public Guid PackageId { get; set; }

            // Redirect PayOS về đúng khu vực FE của người mua (candidate vs employer). Optional —
            // thiếu/không hợp lệ → fallback PayOS:ReturnUrl/CancelUrl (config chung). Chỉ nhận URL http(s) tuyệt đối.
            public string? ReturnUrl { get; set; }
            public string? CancelUrl { get; set; }
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
            // Nullable: invoice-settlement orders and legacy rows may not have a package.
            public string? PackageName { get; set; }
            public Guid? InvoiceId { get; set; }
            public OrderStatus Status { get; set; }
            public long AmountVnd { get; set; }   // khớp Order.AmountVnd (amount_vnd bigint — payment.md §DB)
            // UX3-B1 — số lượt phỏng vấn của gói đơn này mua (biên lai FE đọc order.interviewCredits).
            // NULLABLE, KHÔNG default 0: đơn tất toán hoá đơn (Kind=InvoiceSettlement) không gắn package
            // ⇒ "không mua lượt nào" (null) khác hẳn "gói 0 lượt" (0). Lấy từ Order.Package (navigation
            // đã Include ở GetOrderAsync/GetOwnerOrdersAsync — nơi FE render biên lai + my-orders).
            public int? InterviewCredits { get; set; }
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
                PackageName = order.Package?.Name,
                InvoiceId = order.InvoiceId,
                Status = order.Status,
                AmountVnd = order.AmountVnd,
                InterviewCredits = order.Package?.InterviewCredits,
                PayosOrderCode = order.PayosOrderCode,
                ExpiredAt = order.ExpiredAt,
                PaidAt = order.PaidAt,
                CreatedAt = order.CreatedAt
            };
        }
    }
}
