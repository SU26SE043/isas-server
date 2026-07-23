using PaymentService.Models;

namespace Isas.PaymentService.DTOs
{
    /// <summary>
    /// F18/oversight — item danh sách đơn cho MÀN ADMIN (`GET /payment/admin/orders`). Dùng DTO RIÊNG thay
    /// vì mở rộng <c>OrderResponse</c>: <c>OrderResponse</c> cũng phục vụ <c>GET /order/{id}</c> cho CHỦ đơn,
    /// gắn <c>refundReason</c>/<c>refundGatewayRef</c> vào đó là rò dữ liệu đối soát nội bộ cho khách. Các
    /// field refund ở đây chỉ đi qua endpoint admin-gated.
    /// </summary>
    public class AdminOrderListItem
    {
        public Guid Id { get; set; }
        public OwnerType OwnerType { get; set; }
        public Guid OwnerId { get; set; }
        public OrderKind Kind { get; set; }
        public Guid? PackageId { get; set; }
        public Guid? InvoiceId { get; set; }
        public OrderStatus Status { get; set; }
        public long AmountVnd { get; set; }
        public long PayosOrderCode { get; set; }
        public DateTime ExpiredAt { get; set; }
        public DateTime? PaidAt { get; set; }
        public DateTime CreatedAt { get; set; }

        // ── Refund (chỉ có giá trị với đơn Refunded) ─────────────────────────────────────────────
        public DateTime? RefundedAt { get; set; }
        public string? RefundReason { get; set; }
        public string? RefundGatewayRef { get; set; }
        /// <summary>NULL trên đơn đã Refunded = "chờ chuyển tiền cho khách"; có giá trị = "đã chuyển".</summary>
        public DateTime? RefundSettledAt { get; set; }

        public static AdminOrderListItem From(Order o) => new()
        {
            Id = o.Id,
            OwnerType = o.OwnerType,
            OwnerId = o.OwnerId,
            Kind = o.Kind,
            PackageId = o.PackageId,
            InvoiceId = o.InvoiceId,
            Status = o.Status,
            AmountVnd = o.AmountVnd,
            PayosOrderCode = o.PayosOrderCode,
            ExpiredAt = o.ExpiredAt,
            PaidAt = o.PaidAt,
            CreatedAt = o.CreatedAt,
            RefundedAt = o.RefundedAt,
            RefundReason = o.RefundReason,
            RefundGatewayRef = o.RefundGatewayRef,
            RefundSettledAt = o.RefundSettledAt,
        };
    }

    /// <summary>Lọc theo trạng thái chuyển tiền hoàn cho `GET /payment/admin/orders`.</summary>
    public enum RefundSettlementFilter
    {
        /// <summary>Đơn đã Refunded nhưng CHƯA chuyển tiền (refund_settled_at IS NULL) — việc cần làm.</summary>
        Pending = 1,
        /// <summary>Đơn đã Refunded và đã chuyển tiền (refund_settled_at IS NOT NULL).</summary>
        Settled = 2
    }
}
