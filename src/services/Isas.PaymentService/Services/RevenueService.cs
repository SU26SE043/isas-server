using Isas.PaymentService.DTOs;
using Isas.PaymentService.Models;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
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
        private readonly IAiUsageService _aiUsage;
        private readonly FinanceSettings _finance;

        public RevenueService(PaymentDbContext db) : this(db, null, null)
        {
        }

        /// <summary>
        /// Constructor đầy đủ (dùng bởi DI thật). Hai tham số sau CÓ THỂ null — không phải để "tiện", mà
        /// để giữ nguyên mọi lời gọi <c>new RevenueService(tdb.Db)</c> đã có sẵn trong
        /// <c>RevenueAndLedgerF19Tests</c>/<c>AdminGrantCreditF20Tests</c> (viết từ trước khi có margin/
        /// funnel) tiếp tục biên dịch và chạy đúng: khi null, tự dựng một <see cref="AiUsageService"/> đọc
        /// TRÊN CHÍNH <paramref name="db"/> — vì các test đó không seed <c>ai_usage_logs</c>, kết quả
        /// <see cref="RevenueReportResponse.AiCostVnd"/> tự nhiên ra 0 và không đổi bất kỳ assertion cũ
        /// nào. Đổi hợp đồng constructor mà bắt sửa lại N test không liên quan tới margin/funnel là việc
        /// KHÔNG thuộc phạm vi round này.
        /// </summary>
        public RevenueService(
            PaymentDbContext db, IAiUsageService? aiUsage, IOptions<FinanceSettings>? financeOptions)
        {
            _db = db;
            _aiUsage = aiUsage ?? new AiUsageService(
                db, Options.Create(new AiPricingSettings()), NullLogger<AiUsageService>.Instance);
            _finance = (financeOptions ?? Options.Create(new FinanceSettings())).Value;
        }

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

            // ARPU: đếm chủ ví (owner_id) DISTINCT trong đúng nhóm `collected` ở trên — cùng trục thời
            // gian (paid_at) với gross, để "gross / số người trả" là một phép chia có nghĩa.
            var payingOwnerCount = await collected.Select(o => o.OwnerId).Distinct().CountAsync(ct);
            var arpuVnd = payingOwnerCount == 0 ? 0 : gross / payingOwnerCount;

            var refundRatePct = gross == 0 ? 0.0 : (double)refundedVnd / gross * 100.0;

            // Giá vốn AI: kỳ đo GIỐNG HỆT gross (cùng [from,to)) nhưng KHÔNG cùng trục dữ liệu — chi phí
            // AI khớp theo created_at của dòng ai_usage_logs, không khớp theo đơn hàng nào (một lượt gọi
            // AI không nhất thiết sinh ra một đơn hàng). granularity truyền Day chỉ vì hàm đòi tham số đó;
            // ta chỉ cần TotalCostUsd, không cần buckets của báo cáo AI usage.
            var aiUsage = await _aiUsage.GetReportAsync(from, to, AiUsageGranularity.Day, ct);
            var aiCostUsd = aiUsage.TotalCostUsd;
            var aiCostVnd = (long)Math.Round(aiCostUsd * _finance.UsdToVndRate, MidpointRounding.AwayFromZero);

            // Margin trừ trên NET (tiền THỰC THU sau hoàn), không trừ trên GROSS — biên lợi nhuận phải
            // phản ánh dòng tiền còn lại thật sau khi đã trả lại khách, rồi mới trừ tiếp chi phí vận hành
            // AI. Trừ trên gross sẽ báo margin cao hơn thực tế đúng bằng phần đã hoàn.
            var netRevenueVnd = gross - refundedVnd;
            var grossMarginVnd = netRevenueVnd - aiCostVnd; // CÓ THỂ ÂM — không kẹp về 0 (xem DTO doc).

            // Phễu chuyển đổi: đếm THEO created_at (KHÔNG phải paid_at — status khác Paid/Refunded không
            // có paid_at, đếm theo đó sẽ xoá sạch đơn không sống sót). Query RIÊNG với `collected` vì
            // `collected` lọc theo paid_at và chỉ gồm 2/6 status.
            var funnelCounts = await _db.Orders
                .Where(o => o.CreatedAt >= from && o.CreatedAt < to)
                .GroupBy(o => o.Status)
                .Select(g => new { Status = g.Key, Count = g.Count() })
                .ToListAsync(ct);

            var funnel = new RevenueFunnelRow();
            foreach (var row in funnelCounts)
            {
                funnel.CreatedCount += row.Count;
                switch (row.Status)
                {
                    // Paid VÀ Refunded đều đếm là "đã từng thu tiền" — hoàn tiền không xoá dấu vết đơn
                    // này đã từng chuyển đổi thành công (xem class-doc RevenueFunnelRow).
                    case OrderStatus.Paid:
                    case OrderStatus.Refunded:
                        funnel.PaidCount += row.Count;
                        break;
                    case OrderStatus.Failed:
                        funnel.FailedCount += row.Count;
                        break;
                    case OrderStatus.Expired:
                        funnel.ExpiredCount += row.Count;
                        break;
                    case OrderStatus.Cancelled:
                        funnel.CancelledCount += row.Count;
                        break;
                    case OrderStatus.Pending:
                        funnel.PendingCount += row.Count;
                        break;
                }
            }
            funnel.ConversionRatePct = funnel.CreatedCount == 0
                ? 0.0
                : (double)funnel.PaidCount / funnel.CreatedCount * 100.0;

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
                NetRevenueVnd = netRevenueVnd,
                AiCostUsd = aiCostUsd,
                AiCostVnd = aiCostVnd,
                GrossMarginVnd = grossMarginVnd,
                RefundRatePct = refundRatePct,
                PayingOwnerCount = payingOwnerCount,
                ArpuVnd = arpuVnd,
                Funnel = funnel,
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
