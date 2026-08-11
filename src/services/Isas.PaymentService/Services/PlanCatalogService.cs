using Isas.PaymentService.DTOs;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Options;
using PaymentService.Models;

namespace Isas.PaymentService.Services;

/// <summary>
/// Đường ĐỌC cho người mua: bảng giá công khai + gói đang dùng của chính họ.
///
/// Tách khỏi <see cref="PlanService"/> (CRUD admin + validate catalog) vì hai bên có bất biến ngược nhau:
/// admin phải thấy CẢ gói đã ngừng bán để sửa/bật lại, người mua thì <b>không được</b> thấy — bán một gói
/// <c>is_active=false</c> nghĩa là nhận tiền cho thứ không còn trong danh mục.
/// </summary>
public sealed class PlanCatalogService(
    PaymentDbContext db,
    EntitlementResolver resolver,
    IOptions<TieringSettings> tiering)
{
    private readonly TieringSettings _tiering = tiering.Value;

    public async Task<List<PublicPlanResponse>> GetCatalogAsync(PlanAudience? audience, CancellationToken ct = default)
    {
        var plans = await db.Plans.AsNoTracking()
            .Where(p => p.IsActive && (audience == null || p.Audience == audience))
            .OrderBy(p => p.Audience).ThenBy(p => p.Rank)
            .ToListAsync(ct);
        if (plans.Count == 0) return [];

        // Gói mua được = product_packages Subscription CÒN BÁN trỏ vào plan. Lọc `IsActive` ở cả hai tầng:
        // gói còn trong danh mục nhưng SKU đã ngừng bán thì vẫn hiện (để so sánh quyền lợi), chỉ mất nút Mua.
        var ids = plans.Select(p => p.Id).ToList();
        var packages = await db.ProductPackages.AsNoTracking()
            .Where(pk => pk.IsActive
                      && pk.Type == PackageType.Subscription
                      && pk.PlanId != null
                      && ids.Contains(pk.PlanId!.Value))
            .OrderBy(pk => pk.PriceVnd)
            .ToListAsync(ct);

        return plans
            .Select(p => PublicPlanResponse.From(p, packages.Where(pk => pk.PlanId == p.Id)))
            .ToList();
    }

    public async Task<MyPlanResponse> GetMyPlanAsync(OwnerType ownerType, Guid ownerId, CancellationToken ct = default)
    {
        var ent = await resolver.ResolveAsync(ownerType, ownerId, ct);

        // Tên hiển thị lấy từ catalog; catalog bị sửa/hỏng thì lùi về mã gói chứ KHÔNG ném — endpoint này
        // nằm trên đường vẽ màn hình, hỏng ở đây không đáng làm cả trang trắng.
        var tierName = await db.Plans.AsNoTracking()
            .Where(p => p.Audience == ent.Audience && p.Code == ent.TierCode)
            .Select(p => p.Name)
            .FirstOrDefaultAsync(ct) ?? ent.TierCode;

        DateTime? expiresAt = null;
        if (ent.SubscriptionId is { } sid)
            expiresAt = await db.Subscriptions.AsNoTracking()
                .Where(s => s.Id == sid)
                .Select(s => (DateTime?)s.ExpiresAt)
                .FirstOrDefaultAsync(ct);

        int? used = null, reserved = null, remaining = null;
        DateTime? periodStart = null;
        if (ent is { InterviewFunding: InterviewFunding.Metered, MonthlyQuota: > 0, SubscriptionId: not null })
        {
            // CÙNG hàm mốc kỳ mà ReserveAsync dùng — tự tính lại ngày neo ở đây là cách chắc chắn nhất để
            // FE hiện một kỳ, hệ thống trừ một kỳ khác.
            var ps = CreditAccountService.MeteredPeriodStart(DateTime.UtcNow, ent.MeterAnchorDay);
            var meter = await db.SubscriptionMeters.AsNoTracking()
                .FirstOrDefaultAsync(m => m.SubscriptionId == ent.SubscriptionId!.Value && m.PeriodStart == ps, ct);

            periodStart = ps;
            used = meter?.UsedCount ?? 0;
            reserved = meter?.ReservedCount ?? 0;
            // Khớp guard `used + reserved + 1 <= quota` của ReserveAsync: chỗ đang giữ CŨNG đã tiêu hạn mức.
            remaining = Math.Max(0, ent.MonthlyQuota!.Value - used.Value - reserved.Value);
        }

        return new MyPlanResponse(
            ent.Audience, ent.TierCode, tierName, ent.TierRank, ent.InterviewFunding,
            IsPaid: ent.SubscriptionId is not null,
            ExpiresAt: expiresAt,
            MonthlyQuota: ent.MonthlyQuota,
            QuotaUsed: used, QuotaReserved: reserved, QuotaRemaining: remaining,
            PeriodStart: periodStart,
            TieringEnabled: _tiering.Enabled);
    }
}
