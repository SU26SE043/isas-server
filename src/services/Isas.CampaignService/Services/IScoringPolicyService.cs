using Isas.CampaignService.DTOs;

namespace Isas.CampaignService.Services
{
    /// <summary>SCP1 — đọc/ghi chính sách chấm điểm (HĐ-3). B2 chỉ có phần đọc MẪU hệ thống; tạo
    /// version, preview, apply là các đợt sau.</summary>
    public interface IScoringPolicyService
    {
        /// <summary>HĐ-3 — <c>GET /campaign/scoring-policy-templates</c>: mẫu hệ thống (<c>campaign_id = NULL</c>),
        /// sắp theo loại rồi tên.</summary>
        Task<IReadOnlyList<ScoringPolicyResponse>> GetTemplatesAsync(CancellationToken ct = default);
    }
}
