using Isas.PaymentService.DTOs;
using Microsoft.EntityFrameworkCore;
using PaymentService.Models;

namespace Isas.PaymentService.Services
{
    /// <summary>
    /// F19 — tổng hợp doanh thu. Xem <see cref="RevenueReportResponse"/> cho lý do tách gộp/hoàn.
    ///
    /// Mọi phép cộng được đẩy XUỐNG SQL (<c>GroupBy</c> + <c>Sum</c>) chứ không nạp đơn về rồi cộng trong
    /// bộ nhớ: báo cáo một năm của một hệ thống đang chạy là hàng chục nghìn đơn, và nạp hết về chỉ để
    /// cộng lại là đúng kiểu endpoint chết dần theo thời gian mà không ai để ý.
    /// Gộp theo <c>Year/Month/Day</c> thay vì <c>date_trunc</c> vì <c>date_trunc</c> chỉ có ở Npgsql —
    /// dùng nó thì test SQLite không chạy được câu này nữa, tức phần logic gộp mất hẳn kiểm chứng.
    /// </summary>
    public class RevenueService : IRevenueService
    {
        private readonly PaymentDbContext _db;

        public RevenueService(PaymentDbContext db) => _db = db;

        public async Task<RevenueReportResponse> GetRevenueAsync(
            DateTime from, DateTime to, RevenueGranularity granularity, CancellationToken ct = default)
        {
            // Doanh thu GỘP = tiền ĐÃ THU trong kỳ, neo theo paid_at, ĐỘC LẬP status. Đơn đã hoàn (F18)
            // giữ nguyên paid_at (RefundService chỉ đổi Status→Refunded + set RefundedAt) nên tiền của nó
            // THẬT SỰ đã thu trong kỳ và vẫn thuộc gross; khoản hoàn là dòng RIÊNG, đếm theo refunded_at.
            // Chính cách này mới đạt "không sửa ngược kỳ đã chốt": lọc gross theo status=Paid sẽ làm đơn
            // hoàn RỚT khỏi gross đúng lúc bị hoàn → gross kỳ đã thu tự tụt xuống, rồi net=gross−refunded
            // trừ tác động của khoản hoàn LẦN THỨ HAI (trừ đôi).
            var collected = _db.Orders
                .Where(o => (o.Status == OrderStatus.Paid || o.Status == OrderStatus.Refunded)
                            && o.PaidAt != null && o.PaidAt >= from && o.PaidAt < to);

            var gross = await collected.SumAsync(o => (long?)o.AmountVnd, ct) ?? 0;
            var paidCount = await collected.CountAsync(ct);

            // Tiền hoàn: đếm theo thời điểm HOÀN, không phải thời điểm thu — nếu đếm theo paid_at thì một
            // khoản hoàn hôm nay sẽ đi ngược về sửa báo cáo của kỳ đã chốt.
            var refunded = _db.Orders
                .Where(o => o.Status == OrderStatus.Refunded && o.RefundedAt != null
                            && o.RefundedAt >= from && o.RefundedAt < to);

            var refundedVnd = await refunded.SumAsync(o => (long?)o.AmountVnd, ct) ?? 0;
            var refundedCount = await refunded.CountAsync(ct);

            var byKind = await collected
                .GroupBy(o => o.Kind)
                .Select(g => new RevenueByKindRow
                {
                    Kind = g.Key,
                    AmountVnd = g.Sum(o => (long?)o.AmountVnd) ?? 0,
                    OrderCount = g.Count()
                })
                .ToListAsync(ct);

            var buckets = granularity == RevenueGranularity.Month
                ? await collected
                    .GroupBy(o => new { o.PaidAt!.Value.Year, o.PaidAt!.Value.Month })
                    .Select(g => new
                    {
                        g.Key.Year,
                        g.Key.Month,
                        Day = 1,
                        Amount = g.Sum(o => (long?)o.AmountVnd) ?? 0,
                        Count = g.Count()
                    })
                    .ToListAsync(ct)
                : await collected
                    .GroupBy(o => new { o.PaidAt!.Value.Year, o.PaidAt!.Value.Month, o.PaidAt!.Value.Day })
                    .Select(g => new
                    {
                        g.Key.Year,
                        g.Key.Month,
                        g.Key.Day,
                        Amount = g.Sum(o => (long?)o.AmountVnd) ?? 0,
                        Count = g.Count()
                    })
                    .ToListAsync(ct);

            return new RevenueReportResponse
            {
                From = from,
                To = to,
                Granularity = granularity.ToString(),
                GrossRevenueVnd = gross,
                PaidOrderCount = paidCount,
                RefundedVnd = refundedVnd,
                RefundedOrderCount = refundedCount,
                NetRevenueVnd = gross - refundedVnd,
                ByKind = byKind.OrderByDescending(k => k.AmountVnd).ToList(),
                Buckets = buckets
                    // Dựng lại mốc thời gian ở phía C#: Kind=Utc tường minh để client không phải đoán múi
                    // giờ (Npgsql đọc timestamptz ra Utc, SQLite thì không đảm bảo gì).
                    .Select(b => new RevenueBucketRow
                    {
                        PeriodStart = new DateTime(b.Year, b.Month, b.Day, 0, 0, 0, DateTimeKind.Utc),
                        AmountVnd = b.Amount,
                        OrderCount = b.Count
                    })
                    .OrderBy(b => b.PeriodStart)
                    .ToList()
            };
        }
    }
}
