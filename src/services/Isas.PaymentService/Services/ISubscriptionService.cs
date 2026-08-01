using PaymentService.Models;

namespace Isas.PaymentService.Services
{
    /// <summary>
    /// F8 — vòng đời thuê bao (Premium B2C / membership Tháng·Năm B2B).
    ///
    /// ⚠ Chữ ký cũ (<c>Guid userId</c>) đã bị thay bằng <c>(OwnerType, Guid ownerId)</c>: thuê bao B2B
    /// thuộc về ORG chứ không thuộc cá nhân HR (AUTH-8/PAY-2), giữ <c>userId</c> sẽ gắn membership của
    /// công ty vào đúng người vừa bấm mua. Bản cũ chỉ là stub ném <c>NotImplementedException</c> nên
    /// không có call-site thật nào phải sửa theo.
    /// </summary>
    public interface ISubscriptionService
    {
        /// <summary>
        /// Kích hoạt một kỳ hạn cho <paramref name="orderId"/> (gọi từ webhook Paid, TRONG transaction của
        /// webhook). Idempotent theo <c>order_id</c> (UNIQUE) — redeliver KHÔNG cộng thêm kỳ hạn.
        /// Đơn mua khi thuê bao cũ còn hạn = GIA HẠN: kỳ mới bắt đầu từ ngày hết hạn xa nhất đang có,
        /// không phải từ "bây giờ" (khách không mất những ngày đã trả tiền).
        /// </summary>
        Task<Subscription?> ActivateAsync(
            OwnerType ownerType, Guid ownerId, Guid orderId, ProductPackage package, CancellationToken ct = default);

        /// <summary>
        /// Chủ ví có thuê bao phủ thời điểm hiện tại không (<c>status=Active</c> VÀ <c>expires_at &gt; now</c>).
        /// CỐ Ý không phụ thuộc sweeper đóng dấu <c>Expired</c>: sweeper chết/chậm cũng không được phép
        /// biến một thuê bao đã hết hạn thành quyền vào bài miễn phí.
        /// </summary>
        Task<bool> HasActiveAsync(OwnerType ownerType, Guid ownerId, CancellationToken ct = default);

        /// <summary>Kỳ hạn còn hiệu lực có ngày hết hạn xa nhất (để hiển thị), hoặc null nếu không có.</summary>
        Task<Subscription?> GetActiveAsync(OwnerType ownerType, Guid ownerId, CancellationToken ct = default);

        /// <summary>Cancel only the owner's currently effective tier. Repeated/no-active calls are safe no-ops.</summary>
        Task<SubscriptionCancellationResult> CancelEffectiveAsync(OwnerType ownerType, Guid ownerId, CancellationToken ct = default);
        Task<Subscription> GrantAsync(OwnerType ownerType, Guid ownerId, Guid planId, int durationDays, DateTime? activatedAt, string key, CancellationToken ct = default);

        /// <summary>
        /// Đóng dấu <c>Active → Expired</c> cho các kỳ đã quá hạn. Thuần dọn dẹp/báo cáo — KHÔNG ảnh hưởng
        /// luật vào bài (<see cref="HasActiveAsync"/> tự so ngày). Trả về số row đã đóng dấu.
        /// </summary>
        Task<int> ExpireDueAsync(CancellationToken ct = default);
    }

    public sealed record SubscriptionCancellationResult(Guid? SubscriptionId, bool Cancelled)
    {
        public static readonly SubscriptionCancellationResult NoActive = new(null, false);
    }
}
