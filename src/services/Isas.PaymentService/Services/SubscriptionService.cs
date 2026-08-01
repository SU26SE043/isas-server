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

            // T9 — webhook luôn giữ order Paid khi catalog đã bị sửa sau checkout, nhưng tuyệt đối
            // không cấp một tier sai audience. Snapshot chỉ được tạo từ plan active, hợp lệ lúc này.
            var expectedAudience = ownerType == OwnerType.User ? PlanAudience.B2C : PlanAudience.B2B;
            if (package.PlanId is not Guid planId || package.Audience is not PlanAudience packageAudience)
            {
                _logger?.LogError("Đơn {OrderId}: package {PackageId} thiếu plan/audience → KHÔNG kích hoạt, cần đối soát tay.",
                    orderId, package.Id);
                return null;
            }

            var plan = await _db.Plans.AsNoTracking()
                .SingleOrDefaultAsync(p => p.Id == planId && p.IsActive, ct);
            if (plan is null || packageAudience != expectedAudience || plan.Audience != expectedAudience || plan.Audience != packageAudience)
            {
                _logger?.LogError("Đơn {OrderId}: package {PackageId} / plan {PlanId} không hợp lệ cho {Audience} → KHÔNG kích hoạt, cần đối soát tay.",
                    orderId, package.Id, package.PlanId, expectedAudience);
                return null;
            }

            // Idempotency (PAY-8): webhook redeliver / active-polling P3 chạy lại cùng đơn → kỳ hạn đã có.
            // UNIQUE(order_id) ở DB mới là khoá thật; check này chỉ để trả row cũ thay vì để nổ UNIQUE.
            var already = await _db.Subscriptions.AsNoTracking()
                .FirstOrDefaultAsync(s => s.OrderId == orderId, ct);
            if (already is not null) return already;

            var now = DateTime.UtcNow;

            // T10: effective tier is chosen with exactly the same window/order as entitlement resolution.
            // Higher rank starts now; lower rank is scheduled after the current effective tier. Equal tier
            // is a renewal and extends only that tier's existing active/scheduled chain.
            var effective = await _db.Subscriptions.AsNoTracking()
                .Where(s => s.OwnerType == ownerType && s.OwnerId == ownerId)
                .ActiveAt(now)
                .OrderByTierPriority()
                .FirstOrDefaultAsync(ct);

            DateTime activatedAt;
            if (effective is not null && plan.Rank < effective.TierRank)
            {
                activatedAt = effective.ExpiresAt;
            }
            else if (effective is not null && plan.Rank == effective.TierRank)
            {
                var chainEnd = await _db.Subscriptions.AsNoTracking()
                    .Where(s => s.OwnerType == ownerType && s.OwnerId == ownerId
                                && s.Status == SubscriptionStatus.Active
                                && s.TierCode == plan.Code && s.TierRank == plan.Rank
                                && s.ExpiresAt > now)
                    .MaxAsync(s => (DateTime?)s.ExpiresAt, ct);
                activatedAt = chainEnd is DateTime end && end > now ? end : now;
            }
            else
            {
                // No effective tier, or an upgrade: higher tier takes effect immediately without prorating.
                activatedAt = now;
            }

            var startedAt = activatedAt;
            var snapshot = EntitlementSnapshot.Create(plan);

            var sub = new Subscription
            {
                Id = Guid.NewGuid(),
                OwnerType = ownerType,
                OwnerId = ownerId,
                PlanId = plan.Id,
                Audience = plan.Audience,
                TierCode = plan.Code,
                TierRank = plan.Rank,
                InterviewFunding = plan.InterviewFunding,
                MonthlyQuota = plan.MonthlyQuota,
                EntitlementSnapshot = snapshot.Json,
                EntitlementsVersion = plan.EntitlementsVersion,
                EntitlementHash = snapshot.Hash,
                Source = SubscriptionSource.Purchase,
                ActivatedAt = activatedAt,
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
                .Where(s => s.OwnerType == ownerType && s.OwnerId == ownerId)
                .ActiveAt(now)
                .AnyAsync(ct);
        }

        public Task<Subscription?> GetActiveAsync(OwnerType ownerType, Guid ownerId, CancellationToken ct = default)
        {
            var now = DateTime.UtcNow;
            return _db.Subscriptions.AsNoTracking()
                .Where(s => s.OwnerType == ownerType && s.OwnerId == ownerId)
                .ActiveAt(now)
                .OrderByTierPriority()
                .FirstOrDefaultAsync(ct);
        }

        public async Task<SubscriptionCancellationResult> CancelEffectiveAsync(
            OwnerType ownerType, Guid ownerId, CancellationToken ct = default)
        {
            var now = DateTime.UtcNow;
            // Read the same effective row the resolver sees. The guarded update makes a racing cancel
            // idempotent; only the winner writes the single append-only cancellation event.
            var effective = await _db.Subscriptions.AsNoTracking()
                .Where(s => s.OwnerType == ownerType && s.OwnerId == ownerId)
                .ActiveAt(now)
                .OrderByTierPriority()
                .Select(s => new { s.Id, s.TierRank })
                .FirstOrDefaultAsync(ct);
            if (effective is null) return SubscriptionCancellationResult.NoActive;

            // The route intentionally has no subscription id. If cancelling a higher tier exposes an
            // older lower tier, a retry must remain a no-op rather than cancelling that different row.
            // A later, genuinely higher-tier purchase is still independently cancellable.
            var cancellationAlreadyApplies = await _db.Subscriptions.AsNoTracking()
                .AnyAsync(s => s.OwnerType == ownerType && s.OwnerId == ownerId
                               && s.Status == SubscriptionStatus.Cancelled
                               && s.ExpiresAt > now && s.TierRank >= effective.TierRank, ct);
            if (cancellationAlreadyApplies) return SubscriptionCancellationResult.NoActive;

            var changed = await _db.Subscriptions
                .Where(s => s.Id == effective.Id && s.OwnerType == ownerType && s.OwnerId == ownerId
                            && s.Status == SubscriptionStatus.Active)
                .ExecuteUpdateAsync(u => u
                    .SetProperty(s => s.Status, SubscriptionStatus.Cancelled)
                    .SetProperty(s => s.UpdatedAt, _ => now), ct);
            if (changed == 0) return SubscriptionCancellationResult.NoActive;

            _db.SubscriptionEvents.Add(new SubscriptionEvent
            {
                Id = Guid.NewGuid(), SubscriptionId = effective.Id, EventType = "Cancelled",
                Payload = "{}", CreatedAt = now
            });
            await _db.SaveChangesAsync(ct);
            return new SubscriptionCancellationResult(effective.Id, true);
        }

        public async Task<Subscription> GrantAsync(OwnerType ownerType, Guid ownerId, Guid planId, int durationDays, DateTime? activatedAt, string key, CancellationToken ct = default)
        {
            if (durationDays <= 0 || string.IsNullOrWhiteSpace(key)) throw new ArgumentException("DurationDays and idempotencyKey are required.");
            var old = await _db.Subscriptions.FirstOrDefaultAsync(s => s.OwnerType == ownerType && s.OwnerId == ownerId && s.AdminGrantIdempotencyKey == key, ct); if (old is not null) return old;
            var plan = await _db.Plans.SingleOrDefaultAsync(p => p.Id == planId && p.IsActive, ct) ?? throw new ArgumentException("Plan is not active.");
            if ((ownerType == OwnerType.User) != (plan.Audience == PlanAudience.B2C)) throw new ArgumentException("Plan audience does not match owner.");
            if (!await _db.CreditAccounts.AnyAsync(a => a.OwnerType == ownerType && a.OwnerId == ownerId, ct))
                throw new ArgumentException("Owner must have a credit account before receiving a subscription grant.");
            var at = activatedAt?.ToUniversalTime() ?? DateTime.UtcNow; var snap = EntitlementSnapshot.Create(plan);
            var sub = new Subscription { Id=Guid.NewGuid(), OwnerType=ownerType, OwnerId=ownerId, PlanId=plan.Id, Audience=plan.Audience, TierCode=plan.Code, TierRank=plan.Rank, InterviewFunding=plan.InterviewFunding, MonthlyQuota=plan.MonthlyQuota, EntitlementSnapshot=snap.Json, EntitlementsVersion=plan.EntitlementsVersion, EntitlementHash=snap.Hash, Source=SubscriptionSource.AdminGrant, AdminGrantIdempotencyKey=key, ActivatedAt=at, StartedAt=at, ExpiresAt=at.AddDays(durationDays), BillingCycle=CycleFor(durationDays), CreatedAt=DateTime.UtcNow, UpdatedAt=DateTime.UtcNow };
            _db.Subscriptions.Add(sub); _db.SubscriptionEvents.Add(new SubscriptionEvent { Id=Guid.NewGuid(), SubscriptionId=sub.Id, EventType="Activated", CreatedAt=DateTime.UtcNow }); await _db.SaveChangesAsync(ct); return sub;
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
