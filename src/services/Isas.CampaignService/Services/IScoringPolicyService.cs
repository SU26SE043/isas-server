using Isas.CampaignService.DTOs;
using Isas.Shared.Scoring;

namespace Isas.CampaignService.Services
{
    /// <summary>SCP1 — đọc/ghi chính sách chấm điểm (HĐ-3). B2 = đọc MẪU hệ thống; B3 = kiểm biểu thức.
    /// Tạo version, preview, apply là các đợt sau.</summary>
    public interface IScoringPolicyService
    {
        /// <summary>HĐ-3 — <c>GET /campaign/scoring-policy-templates</c>: mẫu hệ thống (<c>campaign_id = NULL</c>),
        /// sắp theo loại rồi tên.</summary>
        Task<IReadOnlyList<ScoringPolicyResponse>> GetTemplatesAsync(CancellationToken ct = default);

        /// <summary>
        /// HĐ-2 — <c>POST /campaign/{id}/scoring-policies/validate</c>: phân tích biểu thức, kiểm biến
        /// thuộc danh sách cho phép của <paramref name="kind"/>, rồi chạy thử trên BỘ MẪU CỐ ĐỊNH
        /// trong code (<see cref="ScoringContext.Sample"/>). KHÔNG đọc dữ liệu ứng viên, KHÔNG ghi DB.
        /// Chỉ đọc <c>campaigns</c> một lần để xác nhận <paramref name="campaignId"/> thuộc
        /// <paramref name="orgId"/> — ngoài org ⇒ <see cref="KeyNotFoundException"/> (→ 404).
        /// </summary>
        Task<ScoringPolicyValidateResponse> ValidateExpressionAsync(
            Guid orgId, Guid campaignId, ScoringExpressionKind kind, string expression,
            CancellationToken ct = default);
    }
}
