using Isas.PaymentService.DTOs;
using Microsoft.EntityFrameworkCore;
using PaymentService.Models;

namespace Isas.PaymentService.Services
{
    /// <summary>
    /// Chỉ số tài chính kiểu SỐ DƯ (AR + MRR) — xem <see cref="FinanceSnapshotResponse"/> cho lý do tách
    /// hẳn khỏi <see cref="RevenueService"/> (dòng chảy theo kỳ) thay vì thêm tham số vào đó.
    ///
    /// <para><b>AR đẩy xuống SQL</b> — cùng phong cách <see cref="RevenueService"/>: <c>GroupBy</c> +
    /// <c>Sum</c> tại DB, không nạp từng hoá đơn về rồi cộng tay.</para>
    ///
    /// <para><b>MRR CHỦ ĐỘNG group-trong-bộ-nhớ, khác nguyên tắc "đẩy hết xuống SQL" ở trên.</b> Số
    /// subscription đang hoạt động bị chặn trần bởi số KHÁCH ĐANG TRẢ TIỀN thật — không tăng vô hạn theo
    /// lịch sử đơn hàng như bảng <c>orders</c> mà <see cref="RevenueService"/> phải quét hàng năm. Lý do
    /// bắt buộc phải nạp về bộ nhớ: <see cref="Subscription"/> là APPEND-ONLY (mỗi lần mua = 1 row, xem
    /// XML doc của model), nên MỘT chủ ví có thể có NHIỀU row <c>Active</c> chồng lấn cùng lúc (vd nâng
    /// cấp gói giữa kỳ mà kỳ cũ chưa hết hạn). "Subscription hiệu lực" của một chủ ví = row có
    /// <c>TierRank</c> cao nhất (dùng LẠI <see cref="SubscriptionQueryExtensions.OrderByTierPriority"/> —
    /// đúng thứ tự <see cref="EntitlementResolver"/> đang dùng để mở khoá tính năng, để MRR khớp với
    /// "gói khách đang thực sự dùng" chứ không phải một định nghĩa khác). Phép này (sắp trước, group +
    /// lấy phần tử đầu sau) CHỈ đúng khi list đã <c>ToListAsync()</c> — LINQ-to-Objects giữ nguyên thứ tự
    /// trong mỗi nhóm; nếu <c>GroupBy</c> chạy trước khi vật chất hoá thì thứ tự đó không còn gì bảo đảm.</para>
    /// </summary>
    public class FinanceSnapshotService : IFinanceSnapshotService
    {
        private readonly PaymentDbContext _db;

        public FinanceSnapshotService(PaymentDbContext db) => _db = db;

        public async Task<FinanceSnapshotResponse> GetSnapshotAsync(CancellationToken ct = default)
        {
            var now = DateTime.UtcNow;

            // ── AR: công nợ postpaid toàn hệ thống (PlatformAdmin oversight, không lọc theo 1 org) ──
            var arByStatus = await _db.Invoices
                .Where(i => i.Status == InvoiceStatus.Issued || i.Status == InvoiceStatus.Overdue)
                .GroupBy(i => i.Status)
                .Select(g => new { Status = g.Key, Amount = g.Sum(i => i.Amount), Count = g.Count() })
                .ToListAsync(ct);

            var issued = arByStatus.FirstOrDefault(x => x.Status == InvoiceStatus.Issued);
            var overdue = arByStatus.FirstOrDefault(x => x.Status == InvoiceStatus.Overdue);
            var ar = new OutstandingReceivablesRow
            {
                IssuedVnd = issued?.Amount ?? 0m,
                IssuedCount = issued?.Count ?? 0,
                OverdueVnd = overdue?.Amount ?? 0m,
                OverdueCount = overdue?.Count ?? 0,
            };
            ar.TotalVnd = ar.IssuedVnd + ar.OverdueVnd;

            // ── MRR: một row HIỆU LỰC / chủ ví — tránh double-count khi có row Active chồng lấn.
            // Source=AdminGrant KHÔNG phải doanh thu thật (quà cấp tay) → loại ngay từ đầu, cùng tinh
            // thần "credit tặng không bao giờ lọt vào doanh thu" của RevenueService.
            var activeSubs = await _db.Subscriptions
                .Where(s => s.Source == SubscriptionSource.Purchase)
                .ActiveAt(now)
                .OrderByTierPriority()
                .Include(s => s.Package)
                .ToListAsync(ct);

            var effectivePerOwner = activeSubs
                .GroupBy(s => (s.OwnerType, s.OwnerId))
                .Select(g => g.First())   // đã OrderByTierPriority TRƯỚC ToListAsync → First() = hiệu lực nhất
                .ToList();

            // Annual quy về đơn giá THÁNG. PackageId=null (dữ liệu bất thường, chưa từng thấy trên
            // production nhưng không được phép làm sập báo cáo) → Package null-nav → góp 0, KHÔNG crash.
            var mrr = effectivePerOwner.Sum(s =>
                (s.Package?.PriceVnd ?? 0) / (s.BillingCycle == BillingCycle.Annual ? 12m : 1m));

            return new FinanceSnapshotResponse
            {
                AsOf = now,
                OutstandingReceivables = ar,
                MrrVnd = mrr,
                ActiveSubscriptionCount = effectivePerOwner.Count,
            };
        }
    }
}
