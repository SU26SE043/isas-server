using Isas.PaymentService.DTOs;
using Isas.PaymentService.Services;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;

namespace Isas.PaymentService.Controllers
{
    /// <summary>
    /// F22 (FR18) — AUTH-7: tiêu thụ token/chi phí AI cho PlatformAdmin (read-only). Đặt cạnh
    /// <see cref="AdminRevenueController"/> có chủ đích: doanh thu và chi phí AI là hai vế của cùng một câu
    /// hỏi, đọc ở hai service khác nhau thì không ai ghép lại.
    /// Route "admin" → gateway strip /api/v1/payment → /api/v1/payment/admin/ai-usage.
    /// </summary>
    [ApiController]
    [Route("admin")]
    [Authorize(Roles = "Admin")]
    public class AdminAiUsageController : ControllerBase
    {
        private readonly IAiUsageService _usage;

        public AdminAiUsageController(IAiUsageService usage) => _usage = usage;

        /// <summary>
        /// GET /payment/admin/ai-usage?from=&amp;to=&amp;groupBy=day|month
        ///
        /// Kỳ nửa mở <c>[from, to)</c>; thiếu tham số → 30 ngày gần nhất. Mốc thời gian ép về UTC vì client
        /// gửi offset số (<c>+07:00</c>) sẽ cho <c>DateTimeKind.Local</c> và Npgsql từ chối (đúng bug 500 đã
        /// gặp ở <c>POST /api/v1/campaign</c>).
        /// </summary>
        [HttpGet("ai-usage")]
        public async Task<ActionResult<AiUsageReportResponse>> GetAiUsage(
            [FromQuery] DateTime? from = null,
            [FromQuery] DateTime? to = null,
            [FromQuery] string? groupBy = null,
            CancellationToken ct = default)
        {
            var toUtc = ToUtc(to ?? DateTime.UtcNow);
            var fromUtc = ToUtc(from ?? toUtc.AddDays(-30));

            if (fromUtc >= toUtc)
                return BadRequest(new { message = "`from` phải nhỏ hơn `to`." });

            AiUsageGranularity granularity;
            switch ((groupBy ?? "day").Trim().ToLowerInvariant())
            {
                case "day": granularity = AiUsageGranularity.Day; break;
                case "month": granularity = AiUsageGranularity.Month; break;
                default:
                    return BadRequest(new { message = "`groupBy` chỉ nhận `day` hoặc `month`." });
            }

            return Ok(await _usage.GetReportAsync(fromUtc, toUtc, granularity, ct));
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
