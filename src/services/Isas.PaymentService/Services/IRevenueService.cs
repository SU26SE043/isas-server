using Isas.PaymentService.DTOs;

namespace Isas.PaymentService.Services
{
    /// <summary>Độ mịn của chuỗi thời gian trong báo cáo doanh thu.</summary>
    public enum RevenueGranularity
    {
        Day,
        Month
    }

    /// <summary>
    /// F19 — tổng hợp doanh thu cho PlatformAdmin (AUTH-7). Trước vòng này service KHÔNG có endpoint
    /// tổng hợp nào (grep <c>revenue</c>/<c>Sum(</c> trong Payment = 0): admin chỉ xem được danh sách
    /// đơn thô và tự cộng bằng mắt.
    /// </summary>
    public interface IRevenueService
    {
        /// <param name="from">Mốc đầu kỳ (UTC, bao gồm).</param>
        /// <param name="to">Mốc cuối kỳ (UTC, KHÔNG bao gồm) — nửa mở để hai kỳ liền nhau không đếm trùng.</param>
        Task<RevenueReportResponse> GetRevenueAsync(
            DateTime from, DateTime to, RevenueGranularity granularity, CancellationToken ct = default);
    }
}
