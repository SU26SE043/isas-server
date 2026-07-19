using Microsoft.EntityFrameworkCore;
using PaymentService.Models;

namespace Isas.PaymentService.Services
{
    /// <summary>
    /// F8 — hiện thực vòng đời thuê bao. Trước F8 đây là stub <c>NotImplementedException</c> vẫn được
    /// DI-register (bẫy: chạm vào là 500).
    ///
    /// KHÔNG đụng tới ví/sổ cái ở đây. Thuê bao và credit là hai thứ khác loại: credit là hàng tồn kho có
    /// sổ sách, thuê bao là quyền vào bài trong một khoảng thời gian. Trộn hai thứ (kiểu "nạp 9999 credit
    /// khi mua gói tháng") sẽ đẻ ra bút toán khống trong <c>credit_transactions</c> và làm hỏng đúng cái
    /// bất biến <c>remaining + reserved = Σ delta</c> đang được dùng để dò mất tiền.
    /// </summary>
    public class SubscriptionService : ISubscriptionService
    {
        private readonly PaymentDbContext _db;
        private readonly ILogger<SubscriptionService>? _logger;

        public SubscriptionService(PaymentDbContext db, ILogger<SubscriptionService>? logger = null)
        {
            _db = db;
            _logger = logger;
        }

        /// <summary>
        /// Chu kỳ suy từ <c>duration_days</c> của gói. Ngưỡng 180 ngày: gói "tháng" thực tế là 28–31 ngày
        /// (kể cả gói quý 90 ngày vẫn là nhịp tháng), gói "năm" là 365 — không gói nào rơi gần ranh giới
        /// nên ngưỡng rộng này không phân loại nhầm. Chỉ để báo cáo; luật vào bài chỉ nhìn ngày hết hạn.
        /// </summary>
        public static BillingCycle CycleFor(int durationDays) =>
            durationDays >= 180 ? BillingCycle.Annual : BillingCycle.Monthly;

        public async Task<Subscription?> ActivateAsync(
            OwnerType ownerType, Guid ownerId, Guid orderId, ProductPackage package, CancellationToken ct = default)
        {
            // Gói thuê bao BẮT BUỘC có duration_days (PackageService.Validate đã bắt lúc tạo gói). Dữ liệu
            // cũ/sửa tay vẫn có thể lọt null → KHÔNG đoán bừa một hạn mặc định (đoán = bán nhầm thời lượng);
            // ghi log để đối soát và không kích hoạt. Đơn vẫn giữ Paid (tiền đã vào thật) — cùng lối xử lý
            // "gói không sinh credit" của DB20: thà Paid mà thiếu và THẤY ĐƯỢC, còn hơn đơn kẹt Pending.
            if (package.DurationDays is not > 0)
            {
                _logger?.LogError(
                    "Đơn {OrderId}: gói thuê bao {PackageId} không có duration_days hợp lệ ({Days}) → " +
                    "KHÔNG kích hoạt kỳ hạn, cần đối soát tay.",
                    orderId, package.Id, package.DurationDays);
                return null;
            }

            // Idempotency (PAY-8): webhook redeliver / active-polling P3 chạy lại cùng đơn → kỳ hạn đã có.
            // UNIQUE(order_id) ở DB mới là khoá thật; check này chỉ để trả row cũ thay vì để nổ UNIQUE.
            var already = await _db.Subscriptions.AsNoTracking()
                .FirstOrDefaultAsync(s => s.OrderId == orderId, ct);
            if (already is not null) return already;

            var now = DateTime.UtcNow;

            // GIA HẠN: nối tiếp ngày hết hạn xa nhất đang còn hiệu lực chứ không cắt từ "bây giờ" — mua sớm
            // để khỏi quên thì không mất phần ngày đã trả tiền. Hết hạn rồi thì kỳ mới bắt đầu từ now.
            var currentEnd = await _db.Subscriptions
                .Where(s => s.OwnerType == ownerType && s.OwnerId == ownerId
                            && s.Status == SubscriptionStatus.Active
                            && s.ExpiresAt > now)
                .MaxAsync(s => (DateTime?)s.ExpiresAt, ct);

            var startedAt = currentEnd is DateTime end && end > now ? end : now;

            var sub = new Subscription
            {
                Id = Guid.NewGuid(),
                OwnerType = ownerType,
                OwnerId = ownerId,
                PackageId = package.Id,
                OrderId = orderId,
                BillingCycle = CycleFor(package.DurationDays.Value),
                Status = SubscriptionStatus.Active,
                StartedAt = startedAt,
                ExpiresAt = startedAt.AddDays(package.DurationDays.Value),
                CreatedAt = now,
                UpdatedAt = now
            };

            _db.Subscriptions.Add(sub);
            // CỐ Ý không SaveChanges ở đây: hàm chạy BÊN TRONG transaction của WebhookService nên phần ghi
            // được commit chung với flip Pending→Paid ⇒ không có cửa sổ "đơn đã Paid mà chưa có kỳ hạn".
            return sub;
        }

        public Task<bool> HasActiveAsync(OwnerType ownerType, Guid ownerId, CancellationToken ct = default)
        {
            var now = DateTime.UtcNow;
            return _db.Subscriptions.AsNoTracking()
                .AnyAsync(s => s.OwnerType == ownerType && s.OwnerId == ownerId
                               && s.Status == SubscriptionStatus.Active
                               && s.ExpiresAt > now, ct);
        }

        public Task<Subscription?> GetActiveAsync(OwnerType ownerType, Guid ownerId, CancellationToken ct = default)
        {
            var now = DateTime.UtcNow;
            return _db.Subscriptions.AsNoTracking()
                .Where(s => s.OwnerType == ownerType && s.OwnerId == ownerId
                            && s.Status == SubscriptionStatus.Active
                            && s.ExpiresAt > now)
                .OrderByDescending(s => s.ExpiresAt)
                .FirstOrDefaultAsync(ct);
        }

        public Task<int> ExpireDueAsync(CancellationToken ct = default)
        {
            var now = DateTime.UtcNow;
            return _db.Subscriptions
                .Where(s => s.Status == SubscriptionStatus.Active && s.ExpiresAt <= now)
                .ExecuteUpdateAsync(u => u
                    .SetProperty(s => s.Status, SubscriptionStatus.Expired)
                    // DB14 — ExecuteUpdate không đi qua SaveChanges override → stamp tường minh.
                    .SetProperty(s => s.UpdatedAt, _ => DateTime.UtcNow), ct);
        }
    }
}
