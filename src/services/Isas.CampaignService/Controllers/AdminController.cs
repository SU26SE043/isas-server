using Isas.CampaignService.DTOs;
using Isas.CampaignService.Services;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;

namespace Isas.CampaignService.Controllers
{
    /// <summary>
    /// AUTH-7 — PlatformAdmin oversight (read-only, cross-org). Xem MỌI campaign toàn hệ thống
    /// (không lọc theo org của caller). Admin-gated trong service sở hữu dữ liệu. Không mutation.
    /// </summary>
    [ApiController]
    [Route("campaign/admin")]
    [Authorize(Roles = "Admin")]
    public class AdminController : ControllerBase
    {
        private readonly ICampaignService _campaignService;

        public AdminController(ICampaignService campaignService)
        {
            _campaignService = campaignService;
        }

        // GET /campaign/admin/campaigns — mọi campaign (cap 500, mới nhất trước; soft-delete loại tự động).
        // ?status= lọc theo trạng thái (Draft/Active/Closed/Archived); ?orgId= lọc theo org.
        [HttpGet("campaigns")]
        public async Task<ActionResult<List<AdminCampaignListItem>>> ListCampaigns(
            [FromQuery] string? status = null, [FromQuery] Guid? orgId = null, CancellationToken ct = default)
        {
            var campaigns = await _campaignService.ListAllCampaignsAsync(status, orgId, ct);
            return Ok(campaigns);
        }
    }
}
