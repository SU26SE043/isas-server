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
        /// SCP1 · B14 — <c>GET /campaign/{id}/scoring-policies</c>: các version chính sách chấm ĐÃ TẠO
        /// cho <paramref name="campaignId"/> (KHÔNG gồm mẫu hệ thống). Sắp <c>Kind</c> rồi <c>Version</c>
        /// GIẢM DẦN (bản mới nhất lên đầu). "Đang dùng" = version trùng con trỏ
        /// <c>campaigns.{interview,cv}_policy_version</c> (GET /campaign — B13); response KHÔNG mang cờ
        /// nào cho việc đó ⇒ một nguồn sự thật.
        ///
        /// <para><paramref name="kind"/> = bộ lọc TUỲ CHỌN ("Interview" | "CvScreening"); giá trị khác
        /// ⇒ <see cref="System.ArgumentException"/> (→ 400). Campaign ngoài <paramref name="orgId"/> ⇒
        /// <see cref="KeyNotFoundException"/> (→ 404). Chưa có policy nào ⇒ danh sách rỗng (KHÔNG 404).</para>
        /// </summary>
        Task<IReadOnlyList<ScoringPolicyResponse>> ListPoliciesAsync(
            Guid orgId, Guid campaignId, string? kind, CancellationToken ct = default);

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

        /// <summary>
        /// HĐ-4 · B8 — <c>POST /campaign/{id}/scoring-policies/preview</c>: chạy biểu thức đề xuất trên
        /// bó biến của MỌI ứng viên đã chấm (loại <c>kind</c>), trả điểm/hạng cũ↔mới + <c>fingerprint</c>.
        /// Tính LOCAL từ <c>campaign_rankings.scoring_inputs</c> (Interview) / <c>cv_submission</c> +
        /// <c>job_needs</c> (CvScreening) — KHÔNG gọi xuyên service. <b>KHÔNG ghi gì.</b> Hạng tính trên
        /// TOÀN BỘ tập; chỉ phần trả về được phân trang (<paramref name="cursor"/>/<paramref name="limit"/>).
        ///
        /// <para>Ném: <see cref="KeyNotFoundException"/> (campaign ngoài org → 404) ·
        /// <see cref="System.ArgumentException"/> (kind sai → 400) ·
        /// <see cref="ScoringExpressionInvalidException"/> (biểu thức hỏng → 400 kèm errors).</para>
        /// </summary>
        Task<ScoringPolicyPreviewResponse> PreviewPolicyAsync(
            Guid orgId, Guid campaignId, ScoringPolicyPreviewRequest req,
            string? cursor, int? limit, CancellationToken ct = default);

        /// <summary>
        /// HĐ-4/HĐ-6 · B8 — <c>POST /campaign/{id}/scoring-policies/{policyId}/apply</c>: CHỈ OrgAdmin.
        /// So <c>fingerprint</c> body với vân tay tính LẠI từ dòng chính sách đã lưu — lệch ⇒
        /// <see cref="ScoringPolicyChangedException"/> (→ 409 POLICY_CHANGED_AFTER_PREVIEW). Khớp ⇒ ghi
        /// đè điểm chính thức của MỌI ứng viên đã chấm (loại của policy) + ghi <c>audit_logs</c> điểm cũ
        /// + trỏ <c>campaigns.{interview,cv}_policy_version</c> vào version của policy. 1 transaction.
        ///
        /// <para>Ném: <see cref="KeyNotFoundException"/> (campaign/policy ngoài org, hoặc policy là mẫu
        /// hệ thống → 404) · <see cref="EntitlementForbiddenException"/> (không phải OrgAdmin → 403) ·
        /// <see cref="ScoringPolicyChangedException"/> (fingerprint lệch → 409) ·
        /// <see cref="System.InvalidOperationException"/> (chưa có ai được chấm để đánh giá lại → 400).</para>
        /// </summary>
        Task<ApplyScoringPolicyResult> ApplyPolicyAsync(
            Guid orgId, Guid actorUserId, bool isOrgAdmin,
            Guid campaignId, Guid policyId, ApplyScoringPolicyRequest req, CancellationToken ct = default);
    }
}
