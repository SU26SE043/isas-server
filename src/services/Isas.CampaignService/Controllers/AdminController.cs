using Isas.CampaignService.DTOs;
using Isas.CampaignService.Services;
using Isas.Shared.Pagination;
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

        // GET /campaign/admin/campaigns — mọi campaign (mới nhất trước; keyset-paged DB8; soft-delete loại tự động).
        // ?status= lọc theo trạng thái (Draft/Active/Closed/Archived); ?orgId= lọc theo org.
        // ?limit= (mặc định/tối đa 500) + ?cursor= (opaque) để phân trang; next-cursor trả ở header
        // X-Next-Cursor (vắng = hết trang). Body giữ nguyên mảng JSON (backward-compat cho FE).
        [HttpGet("campaigns")]
        public async Task<ActionResult<List<AdminCampaignListItem>>> ListCampaigns(
            [FromQuery] string? status = null, [FromQuery] Guid? orgId = null,
            [FromQuery] string? cursor = null, [FromQuery] int? limit = null, CancellationToken ct = default)
        {
            var page = await _campaignService.ListAllCampaignsAsync(status, orgId, cursor, limit, ct);
            if (page.NextCursor is not null)
                Response.Headers[KeysetPaging.NextCursorHeader] = page.NextCursor;
            return Ok(page.Items);
        }
    }
}
