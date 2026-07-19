using Isas.CampaignService.DTOs;
using Isas.CampaignService.Services;
using Isas.Shared.Pagination;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.RateLimiting;

namespace Isas.CampaignService.Controllers
{
    /// <summary>
    /// F17 (FR14) — **Public API** cho bên thứ ba (ATS: Greenhouse/Lever/Workday…) đọc kết quả
    /// campaign. Xác thực bằng <c>X-Api-Key</c>, KHÔNG phải JWT (bên gọi là hệ thống, không có phiên
    /// người dùng).
    ///
    /// **Qua gateway** (GEN-1): route <c>campaign/public/**</c> nằm dưới catch-all
    /// <c>/api/v1/campaign/{**}</c> sẵn có ⇒ URL công khai <c>/api/v1/campaign/public/…</c>, KHÔNG
    /// cần thêm route gateway. Đây là API *public* của bên ngoài gọi vào, khác hẳn <c>/internal/*</c>
    /// (service-to-service, đi thẳng, không qua gateway) — nên đi qua gateway là ĐÚNG chỗ: có TLS
    /// termination, log truy cập tập trung và một bề mặt vào duy nhất.
    ///
    /// **Phạm vi org**: org của key (claim do handler gắn từ hàng DB) được truyền thẳng vào ĐÚNG
    /// những service method mà đường JWT dùng — <c>GetCampaignsAsync(orgId,…)</c> /
    /// <c>GetCampaignResultsAsync(orgId,…)</c>, cả hai kẹp <c>c.OrgId == orgId</c> ngay trong vị ngữ
    /// SQL. Cố ý KHÔNG viết truy vấn song song cho đường public: một chỗ lọc org = một chỗ để sai.
    /// </summary>
    [ApiController]
    [Route("campaign/public")]
    [Authorize(AuthenticationSchemes = ApiKeyDefaults.Scheme)]   // JWT KHÔNG mở được các endpoint này
    [EnableRateLimiting(ApiKeyDefaults.RateLimitPolicy)]
    public class PublicApiController : ControllerBase
    {
        private readonly ICampaignService _campaignService;
        private readonly ILogger<PublicApiController> _logger;

        public PublicApiController(ICampaignService campaignService, ILogger<PublicApiController> logger)
        {
            _campaignService = campaignService;
            _logger = logger;
        }

        /// <summary>
        /// Org gắn với API key đã xác thực. Đọc từ claim do <see cref="ApiKeyAuthenticationHandler"/>
        /// gắn (nguồn = hàng <c>api_keys</c>), KHÔNG từ input client. Không có claim = lỗi lập trình
        /// (endpoint đã [Authorize] scheme ApiKey) → null → 403, fail-closed.
        /// </summary>
        private Guid? GetKeyOrgId()
            => Guid.TryParse(User.FindFirst(ApiKeyDefaults.OrgIdClaim)?.Value, out var g) ? g : (Guid?)null;

        private bool KeyAllowsPii()
            => User.FindFirst(ApiKeyDefaults.IncludePiiClaim)?.Value == "true";

        /// <summary>
        /// GET /api/v1/campaign/public/campaigns — campaign của org sở hữu key (mới nhất trước).
        /// Keyset-paged như các list khác (DB31): <c>?cursor=&amp;limit=</c>, next-cursor ở header
        /// <c>X-Next-Cursor</c>.
        /// </summary>
        [HttpGet("campaigns")]
        public async Task<ActionResult<List<PublicCampaignSummary>>> ListCampaigns(
            [FromQuery] string? cursor = null, [FromQuery] int? limit = null, CancellationToken ct = default)
        {
            var orgId = GetKeyOrgId();
            if (orgId is null) return Forbid();

            var page = await _campaignService.GetCampaignsAsync(orgId.Value, cursor, limit, ct);
            if (page.NextCursor is not null)
                Response.Headers[KeysetPaging.NextCursorHeader] = page.NextCursor;

            // Thu hẹp shape: JD text, tiêu chí, câu hỏi, cờ anti-cheat… là nội bộ org, ATS không cần.
            var items = page.Items.Select(c => new PublicCampaignSummary
            {
                Id = c.Id,
                Title = c.Title,
                Status = c.Status,
                CreatedAt = c.CreatedAt,
                ExpiresAt = c.ExpiresAt
            }).ToList();

            return Ok(items);
        }

        /// <summary>
        /// GET /api/v1/campaign/public/campaigns/{id}/results — bảng kết quả + xếp hạng.
        /// Campaign của org KHÁC → 404 (giống đường JWT: không xác nhận hộ campaign đó có tồn tại).
        /// </summary>
        [HttpGet("campaigns/{id:guid}/results")]
        public async Task<ActionResult<PublicCampaignResultsResponse>> GetCampaignResults(
            Guid id, CancellationToken ct)
        {
            var orgId = GetKeyOrgId();
            if (orgId is null) return Forbid();

            try
            {
                // CÙNG method đường JWT dùng → cùng vị ngữ org. Không nhân bản truy vấn.
                var results = await _campaignService.GetCampaignResultsAsync(orgId.Value, id, ct);
                var pii = KeyAllowsPii();

                return Ok(new PublicCampaignResultsResponse
                {
                    CampaignId = results.CampaignId,
                    PassScorePct = results.PassScorePct,
                    TotalCandidates = results.TotalCandidates,
                    PiiIncluded = pii,
                    Results = results.Results.Select(r => new PublicCampaignResultRow
                    {
                        Rank = r.Rank,
                        CandidateId = r.CandidateId,
                        SessionId = r.SessionId,
                        // Deny-by-default: key không bật includePii → không trả tên/email.
                        FullName = pii ? r.FullName : null,
                        Email = pii ? r.Email : null,
                        TotalScore = r.TotalScore,
                        Result = r.Result,
                        // Chỉ nói "đã có người xem lại", KHÔNG lộ overrideNote (ghi chú riêng của HR)
                        // và KHÔNG lộ flags[] chống gian lận (CAMP-12/D13: cờ là để HR đọc và tự đánh
                        // giá; đẩy sang ATS là mời auto-loại đúng thứ D13 cấm).
                        HrReviewed = r.OverriddenAt != null,
                        ScoredAt = r.ScoredAt
                    }).ToList()
                });
            }
            catch (KeyNotFoundException)
            {
                _logger.LogInformation(
                    "API key của org {OrgId} hỏi campaign {CampaignId} không thuộc org.", orgId, id);
                return NotFound();
            }
            catch (Exception ex)
            {
                return StatusCode(500, $"Failed to get results: {ex.Message}");
            }
        }
    }
}
