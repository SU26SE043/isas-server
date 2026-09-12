using Isas.CampaignService.DTOs;
using Isas.CampaignService.Services;
using Isas.Shared.Analytics;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using System.Security.Claims;

namespace Isas.CampaignService.Controllers
{
    /// <summary>
    /// Phân tích tuyển dụng theo TỔ CHỨC cho employer — bản ORG-SCOPED của <c>AdminController.Analytics</c>
    /// (FR18 platform-wide, KHÔNG sửa). Tách khỏi <c>CampaignController</c> để không chọi file, nhưng dùng
    /// CÙNG prefix <c>[Route("campaign")]</c>: route literal <c>campaign/analytics</c> đứng độc lập với
    /// <c>[HttpGet("{id}")]</c> của CampaignController — ASP.NET Core xếp segment literal ưu tiên hơn segment
    /// tham số (<c>/Products/List</c> thắng <c>/Products/{id}</c>), nên GET /campaign/analytics KHÔNG rơi vào
    /// GetCampaignById. Có test khoá attribute bằng reflection; L3 phải verify thật trên dev.
    ///
    /// <para><c>Roles = "Employer"</c> — cả OrgAdmin lẫn HrMember (đọc, không phải billing — AUTH-6 chỉ chặn
    /// money-mutation). <c>org_id</c> từ claim JWT (AUTH-8, BK4); thiếu ⇒ 403 như <c>GetAllCampaign</c>.</para>
    /// </summary>
    [ApiController]
    [Route("campaign")]
    [Authorize(Roles = "Employer")]
    public class CampaignAnalyticsController : ControllerBase
    {
        private readonly ICampaignAnalyticsService _analytics;

        public CampaignAnalyticsController(ICampaignAnalyticsService analytics)
        {
            _analytics = analytics;
        }

        private static readonly IReadOnlyDictionary<string, AnalyticsGranularity> AnalyticsGranularities =
            new Dictionary<string, AnalyticsGranularity> { ["day"] = AnalyticsGranularity.Day, ["month"] = AnalyticsGranularity.Month };

        // BK4: chủ sở hữu campaign = ORG. Cùng cách đọc claim như CampaignController.GetOrgId().
        private Guid? GetOrgId()
            => Guid.TryParse(User.FindFirstValue("org_id"), out var g) ? g : (Guid?)null;

        // GET /campaign/analytics?from=&to=&groupBy=day|month — kỳ mặc định 30 ngày gần nhất, nửa mở [from, to).
        // Kỳ CHỈ áp cho `buckets`; các khối còn lại là trạng thái HIỆN TẠI của cả org (stock vs flow).
        [HttpGet("analytics")]
        public async Task<ActionResult<CampaignAnalyticsResponse>> Analytics(
            [FromQuery] DateTime? from = null, [FromQuery] DateTime? to = null,
            [FromQuery] string? groupBy = null, CancellationToken ct = default)
        {
            var orgId = GetOrgId();
            if (orgId is null)
                return Forbid();

            if (!AnalyticsPeriod.TryResolve(from, to, groupBy, AnalyticsGranularities, out var period, out var error))
                return BadRequest(new
                {
                    message = error == AnalyticsPeriodError.InvalidRange
                        ? "`from` phải nhỏ hơn `to`."
                        : "`groupBy` chỉ nhận `day` hoặc `month`."
                });

            var result = await _analytics.GetAsync(orgId.Value, period!, ct);
            return Ok(result);
        }
    }
}
