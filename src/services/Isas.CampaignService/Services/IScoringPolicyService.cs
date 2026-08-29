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

        /// <summary>
        /// HĐ-3 · B4 — <c>POST /campaign/{id}/scoring-policies</c>: tạo version MỚI cho campaign, khởi
        /// từ mẫu (<c>sourceTemplateId</c>) hoặc biểu thức tự gõ. CHÉP giá trị (không tham chiếu sống
        /// tới mẫu — CAMP-20), validate lại bằng đường B3, rồi trỏ
        /// <c>campaigns.{interview,cv}_policy_version</c> vào version vừa tạo.
        ///
        /// <para>Ném: <see cref="KeyNotFoundException"/> (campaign ngoài org → 404) ·
        /// <see cref="System.ArgumentException"/> (kind/name/sourceTemplateId sai → 400) ·
        /// <see cref="ScoringExpressionInvalidException"/> (biểu thức hỏng → 400 kèm errors) ·
        /// <see cref="EntitlementForbiddenException"/> (không-Draft mà không phải OrgAdmin → 403, HĐ-6) ·
        /// <see cref="System.InvalidOperationException"/> (chiến dịch đã đóng, hoặc đã có người được
        /// chấm ⇒ phải qua xem trước B8 → 409).</para>
        /// </summary>
        Task<ScoringPolicyResponse> CreatePolicyAsync(
            Guid orgId, Guid actorUserId, bool isOrgAdmin,
            Guid campaignId, CreateScoringPolicyRequest req, CancellationToken ct = default);
    }
}
