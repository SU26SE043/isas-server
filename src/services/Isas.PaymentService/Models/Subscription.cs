namespace PaymentService.Models
{
    /// <summary>
    /// F8 — gói thuê bao (Premium B2C / membership Tháng·Năm B2B). Thay cho bảng <c>subscriptions</c> cũ
    /// đã bị DROP ở DB15 vì là dead scaffold (0 query dùng); lần này bảng được dựng lại CÙNG với đường
    /// tiêu thụ thật (order → webhook → activate → gate ở reserve).
    ///
    /// MỖI LẦN MUA = MỘT ROW (append-one-per-order), KHÔNG sửa row cũ để "kéo dài hạn":
    ///   • idempotency của webhook nằm ở UNIQUE(order_id) — cơ chế cùng loại với UNIQUE(session_id) của
    ///     <see cref="CreditReservation"/>. Nếu gia hạn là UPDATE row cũ thì webhook redeliver phải tự
    ///     nhớ "đã cộng hạn cho đơn này chưa", tức là phát minh lại một khoá idempotency yếu hơn.
    ///   • giữ được lịch sử mua/gia hạn để đối soát (append-only, cùng tinh thần credit_transactions).
    /// "Đang có thuê bao" = TỒN TẠI row <c>Active</c> mà <c>expires_at</c> còn ở tương lai (các row có
    /// thể chồng lấn — chỉ cần phủ được thời điểm đang xét).
    /// </summary>
    public class Subscription : IHasUpdatedAt
    {
        public Guid Id { get; set; }

        // Chủ thuê bao theo owner model (D15): Org = membership B2B · User = Premium B2C.
        public OwnerType OwnerType { get; set; }
        public Guid OwnerId { get; set; }

        /// <summary>Gói đã mua (ref cùng service → product_packages). Nullable để không mất row thuê bao
        /// nếu sau này gói bị dọn; FK Restrict nên thực tế không xoá được khi còn tham chiếu.</summary>
        public Guid? PackageId { get; set; }

        /// <summary>
        /// Đơn hàng sinh ra kỳ hạn này. <b>UNIQUE (filtered NOT NULL)</b> = khoá idempotency của webhook:
        /// PayOS redeliver cùng một đơn sẽ đụng UNIQUE thay vì cộng thêm một kỳ hạn miễn phí.
        /// </summary>
        public Guid? OrderId { get; set; }

        public BillingCycle BillingCycle { get; set; }
        public SubscriptionStatus Status { get; set; } = SubscriptionStatus.Active;

        public DateTime StartedAt { get; set; }
        public DateTime ExpiresAt { get; set; }

        public DateTime CreatedAt { get; set; }
        public DateTime UpdatedAt { get; set; } = DateTime.UtcNow;

        public ProductPackage? Package { get; set; }
        public Order? Order { get; set; }
    }

    /// <summary>
    /// Chu kỳ thanh toán — suy ra từ <c>product_packages.duration_days</c> lúc kích hoạt (xem
    /// <c>SubscriptionService.CycleFor</c>), lưu lại thành cột để báo cáo/đối soát không phải suy lại.
    /// </summary>
    public enum BillingCycle
    {
        Monthly,
        Annual
    }

    public enum SubscriptionStatus
    {
        /// <summary>Kỳ hạn đang chạy. CHỈ trạng thái này + <c>expires_at &gt; now</c> mới mở khoá unlimited.</summary>
        Active,
        /// <summary>Đã quá <c>expires_at</c> (do sweeper đóng dấu). Chỉ để báo cáo — luật vào bài vẫn so ngày.</summary>
        Expired,
        /// <summary>Bị huỷ giữa kỳ (hoàn tiền/đối soát tay). Chặn unlimited NGAY, không đợi hết hạn.</summary>
        Cancelled
    }
}
