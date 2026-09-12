using Isas.CampaignService.DTOs;
using Isas.Shared.Analytics;

namespace Isas.CampaignService.Services
{
    /// <summary>Phân tích tuyển dụng theo TỔ CHỨC cho employer — `GET /campaign/analytics`.</summary>
    public interface ICampaignAnalyticsService
    {
        /// <param name="orgId">Org của caller (claim `org_id`) — mọi khối chỉ đếm campaign của org này.</param>
        /// <param name="period">Kỳ nửa mở [From, To) + granularity đã chuẩn hoá (chỉ áp cho `buckets`).</param>
        Task<CampaignAnalyticsResponse> GetAsync(Guid orgId, AnalyticsPeriodResult period, CancellationToken ct);
    }
}
