using Isas.PaymentService.DTOs;
using Isas.PaymentService.Services;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;

namespace Isas.PaymentService.Controllers
{
    /// <summary>
    /// F19 — AUTH-7: dashboard doanh thu cho PlatformAdmin (read-only, xuyên chủ ví). Admin-gated trong
    /// service sở hữu dữ liệu. Route "admin" → gateway strip /api/v1/payment → /api/v1/payment/admin/revenue.
    /// </summary>
    [ApiController]
    [Route("admin")]
    [Authorize(Roles = "Admin")]
    public class AdminRevenueController : ControllerBase
    {
        private readonly IRevenueService _revenue;

        public AdminRevenueController(IRevenueService revenue) => _revenue = revenue;

        /// <summary>
        /// GET /payment/admin/revenue?from=&amp;to=&amp;groupBy=day|month
        ///
        /// Kỳ là nửa mở <c>[from, to)</c> — hai kỳ liền nhau không đếm trùng một đơn. Thiếu tham số →
        /// 30 ngày gần nhất. Mốc thời gian được ép về UTC: client gửi `+07:00` thì
        /// <c>DateTimeKind.Local</c> sẽ khiến Npgsql từ chối (đúng bug 500 đã gặp ở
        /// `POST /api/v1/campaign` khi offset là số thay vì `Z`).
        /// </summary>
        [HttpGet("revenue")]
        public async Task<ActionResult<RevenueReportResponse>> GetRevenue(
            [FromQuery] DateTime? from = null,
            [FromQuery] DateTime? to = null,
            [FromQuery] string? groupBy = null,
            CancellationToken ct = default)
        {
            var toUtc = ToUtc(to ?? DateTime.UtcNow);
            var fromUtc = ToUtc(from ?? toUtc.AddDays(-30));

            if (fromUtc >= toUtc)
                return BadRequest(new { message = "`from` phải nhỏ hơn `to`." });

            RevenueGranularity granularity;
            switch ((groupBy ?? "day").Trim().ToLowerInvariant())
            {
                case "day": granularity = RevenueGranularity.Day; break;
                case "month": granularity = RevenueGranularity.Month; break;
                default:
                    return BadRequest(new { message = "`groupBy` chỉ nhận `day` hoặc `month`." });
            }

            return Ok(await _revenue.GetRevenueAsync(fromUtc, toUtc, granularity, ct));
        }

        // Unspecified = client gửi chuỗi không offset → coi như đã là UTC (mọi mốc trong DB là UTC).
        // Local = client gửi offset số (+07:00) → quy đổi thật sự, đừng gán nhãn suông.
        private static DateTime ToUtc(DateTime value) => value.Kind switch
        {
            DateTimeKind.Utc => value,
            DateTimeKind.Local => value.ToUniversalTime(),
            _ => DateTime.SpecifyKind(value, DateTimeKind.Utc)
        };
    }
}
