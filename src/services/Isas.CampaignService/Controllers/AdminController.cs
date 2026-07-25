using Isas.CampaignService.DTOs;
using Isas.CampaignService.Services;
using Isas.Shared.Pagination;
using Isas.Shared.Analytics;
using Isas.CampaignService.Models;
using Microsoft.EntityFrameworkCore;
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

        private static readonly IReadOnlyDictionary<string, AnalyticsGranularity> AnalyticsGranularities =
            new Dictionary<string, AnalyticsGranularity> { ["day"] = AnalyticsGranularity.Day, ["month"] = AnalyticsGranularity.Month };

        /// <summary>FR18 — funnel B2B; global query filter cố ý loại campaign đã soft-delete.</summary>
        [HttpGet("analytics")]
        public async Task<IActionResult> Analytics(
            [FromServices] CampaignDbContext db, [FromQuery] DateTime? from = null, [FromQuery] DateTime? to = null,
            [FromQuery] string? groupBy = null, CancellationToken ct = default)
        {
            if (!AnalyticsPeriod.TryResolve(from, to, groupBy, AnalyticsGranularities, out var period, out var error))
                return BadRequest(new { message = error == AnalyticsPeriodError.InvalidRange ? "`from` phải nhỏ hơn `to`." : "`groupBy` chỉ nhận `day` hoặc `month`." });
            var p = period!;
            var campaigns = await db.Campaigns.AsNoTracking().ToListAsync(ct);
            var invitations = await db.CampaignInvitations.AsNoTracking().ToListAsync(ct);
            var members = await db.CampaignMemberships.AsNoTracking().ToListAsync(ct);
            var flags = await db.SessionFlags.AsNoTracking().ToListAsync(ct);
            var buckets = campaigns.Where(x => x.CreatedAt >= p.FromUtc && x.CreatedAt < p.ToUtc)
                .Select(x => (Key: AnalyticsPeriod.BucketKey(x.CreatedAt, p.Granularity), Campaigns: 1, Invitations: 0, Joins: 0, Started: 0))
                .Concat(invitations.Where(x => x.CreatedAt >= p.FromUtc && x.CreatedAt < p.ToUtc).Select(x => (Key: AnalyticsPeriod.BucketKey(x.CreatedAt, p.Granularity), Campaigns: 0, Invitations: 1, Joins: 0, Started: 0)))
                .Concat(members.Where(x => x.JoinedAt >= p.FromUtc && x.JoinedAt < p.ToUtc).Select(x => (Key: AnalyticsPeriod.BucketKey(x.JoinedAt!.Value, p.Granularity), Campaigns: 0, Invitations: 0, Joins: 1, Started: 0)))
                .Concat(members.Where(x => x.SessionId != null && x.UpdatedAt >= p.FromUtc && x.UpdatedAt < p.ToUtc).Select(x => (Key: AnalyticsPeriod.BucketKey(x.UpdatedAt, p.Granularity), Campaigns: 0, Invitations: 0, Joins: 0, Started: 1)))
                .GroupBy(x => x.Key).OrderBy(x => x.Key).Select(x => new { periodStart = AnalyticsPeriod.BucketStart(x.Key, p.Granularity), campaignsCreated = x.Sum(v => v.Campaigns), invitationsCreated = x.Sum(v => v.Invitations), joins = x.Sum(v => v.Joins), interviewsStarted = x.Sum(v => v.Started) });
            return Ok(new { from = p.FromUtc, to = p.ToUtc, granularity = p.Granularity.ToString().ToLowerInvariant(), totals = new { byStatus = campaigns.GroupBy(x => x.Status.ToString()).Select(x => new { status = x.Key, count = x.Count() }), invitationsSent = invitations.Count(x => x.EmailSentAt != null), invitationsUnsent = invitations.Count(x => x.EmailSentAt == null), flagsBySignal = flags.GroupBy(x => x.SignalType).Select(x => new { signalType = x.Key, count = x.Count() }) }, buckets });
        }
    }
}
