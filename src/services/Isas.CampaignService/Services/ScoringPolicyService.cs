using Isas.CampaignService.DTOs;
using Isas.CampaignService.Models;
using Isas.Shared.Scoring;
using Microsoft.EntityFrameworkCore;

namespace Isas.CampaignService.Services
{
    /// <inheritdoc />
    public sealed class ScoringPolicyService : IScoringPolicyService
    {
        private readonly CampaignDbContext _db;

        public ScoringPolicyService(CampaignDbContext db) => _db = db;

        public async Task<ScoringPolicyValidateResponse> ValidateExpressionAsync(
            Guid orgId, Guid campaignId, ScoringExpressionKind kind, string expression,
            CancellationToken ct = default)
        {
            // Chỉ để chặn dò campaign của org khác — 1 index probe, KHÔNG chạm dữ liệu ứng viên.
            // Query filter soft-delete (D11) tự lọc ⇒ campaign đã xoá cũng ra 404.
            var owned = await _db.Campaigns.AnyAsync(c => c.Id == campaignId && c.OrgId == orgId, ct);
            if (!owned) throw new KeyNotFoundException($"Campaign {campaignId} not found.");

            // BỘ MẪU nằm trong code (ScoringContext.Sample) — endpoint chạy được cả khi campaign chưa
            // có ứng viên nào. Không ghi gì.
            var r = ScoringExpression.Validate(kind, expression ?? string.Empty);
            return new ScoringPolicyValidateResponse(
                r.Valid,
                r.Valid ? r.SampleScore : null,
                r.Valid ? null : r.Errors);
        }

        public async Task<IReadOnlyList<ScoringPolicyResponse>> GetTemplatesAsync(CancellationToken ct = default)
        {
            // Chỉ mẫu hệ thống. Materialize rồi sắp + map trong bộ nhớ (≤ vài chục dòng):
            //   · sắp theo `Kind` = giá trị ENUM (Interview 0 trước CvScreening 1), KHÔNG theo chuỗi
            //     đã convert — cột lưu "CvScreening"/"Interview" nên ORDER BY ở SQL sẽ ra C trước I,
            //     lệ thuộc collation DB. Sắp trong bộ nhớ cho tất định giữa SQLite (test) và Postgres.
            //   · `Kind.ToString()` cũng để trong bộ nhớ, không nhờ EF dịch.
            var rows = await _db.ScoringPolicies
                .AsNoTracking()
                .Where(p => p.CampaignId == null)
                .ToListAsync(ct);

            return rows
                .OrderBy(p => p.Kind)
                .ThenBy(p => p.Name, StringComparer.Ordinal)
                .Select(Map)
                .ToList();
        }

        public async Task<ScoringPolicyResponse> CreatePolicyAsync(
            Guid orgId, Guid actorUserId, bool isOrgAdmin,
            Guid campaignId, CreateScoringPolicyRequest req, CancellationToken ct = default)
        {
            var kind = req.Kind switch
            {
                "Interview" => ScoringExpressionKind.Interview,
                "CvScreening" => ScoringExpressionKind.CvScreening,
                _ => throw new ArgumentException("kind phải là 'Interview' hoặc 'CvScreening'."),
            };
            if (string.IsNullOrWhiteSpace(req.Name))
                throw new ArgumentException("name là bắt buộc.");
            var expression = req.Expression ?? string.Empty;

            var campaign = await _db.Campaigns
                .FirstOrDefaultAsync(c => c.Id == campaignId && c.OrgId == orgId, ct)
                ?? throw new KeyNotFoundException($"Campaign {campaignId} not found.");

            if (campaign.Status is CampaignStatus.Closed or CampaignStatus.Archived)
                throw new InvalidOperationException("Chiến dịch đã đóng — không tạo chính sách chấm mới.");

            // CẤM B4 — đã có người được chấm (theo LOẠI): tạo version mới lúc này đổi kết quả người ta
            // ⇒ phải qua xem-trước-rồi-áp của B8.
            var hasScored = kind == ScoringExpressionKind.Interview
                ? await _db.CampaignRankings.AnyAsync(r => r.CampaignId == campaignId, ct)
                : await _db.CvSubmissions.AnyAsync(s => s.CampaignId == campaignId && s.OverallMatchScore != null, ct);
            if (hasScored)
                throw new InvalidOperationException(
                    "POLICY_NEEDS_PREVIEW: chiến dịch đã có người được chấm — phải xem trước rồi mới áp (B8).");

            // HĐ-6 — HrMember chỉ sửa chính sách chấm khi campaign còn Draft.
            if (campaign.Status != CampaignStatus.Draft && !isOrgAdmin)
                throw new EntitlementForbiddenException(
                    "HrMember chỉ sửa chính sách chấm khi chiến dịch còn Draft (HĐ-6).");

            // sourceTemplateId — PROVENANCE. Nếu có: phải là mẫu hệ thống (campaign_id NULL) cùng loại.
            // KHÔNG đọc giá trị từ mẫu ở đây (client gửi giá trị đã chỉnh trong body); chỉ lưu id làm dấu.
            if (req.SourceTemplateId is Guid tid)
            {
                var okTemplate = await _db.ScoringPolicies
                    .AnyAsync(p => p.Id == tid && p.CampaignId == null && p.Kind == kind, ct);
                if (!okTemplate)
                    throw new ArgumentException("sourceTemplateId không phải mẫu hệ thống hợp lệ của cùng loại.");
            }

            // B3 — validate lại, KHÔNG tin dữ liệu vào.
            var check = ScoringExpression.Validate(kind, expression);
            if (!check.Valid)
                throw new ScoringExpressionInvalidException(check.Errors);

            var maxVersion = await _db.ScoringPolicies
                .Where(p => p.CampaignId == campaignId && p.Kind == kind)
                .Select(p => (int?)p.Version)
                .MaxAsync(ct) ?? 0;

            var policy = new ScoringPolicy
            {
                Id = Guid.NewGuid(),
                CampaignId = campaignId,
                Kind = kind,
                Version = maxVersion + 1,
                EngineVersion = ScoringEngine.Version,   // engine HIỆN TẠI — KHÔNG chép từ mẫu
                Name = req.Name!.Trim(),
                Description = string.IsNullOrWhiteSpace(req.Description) ? null : req.Description!.Trim(),
                Expression = expression,                 // giá trị trong body — KHÔNG deref mẫu
                PassScorePct = req.PassScorePct,
                SourceTemplateId = req.SourceTemplateId, // dấu vết provenance, KHÔNG FK sống
                CreatedAt = DateTime.UtcNow,
                CreatedBy = actorUserId,
            };
            _db.ScoringPolicies.Add(policy);

            // B4 chỉ chạy khi 0 người được chấm ⇒ trỏ con trỏ vào version vừa tạo là an toàn (không có
            // kết quả cũ để relabel). Đổi thước đo cho campaign đã chấm là việc của B8.
            if (kind == ScoringExpressionKind.Interview) campaign.InterviewPolicyVersion = policy.Version;
            else campaign.CvPolicyVersion = policy.Version;

            await _db.SaveChangesAsync(ct);
            return Map(policy);
        }

        private static ScoringPolicyResponse Map(ScoringPolicy p) => new(
            p.Id,
            p.Kind.ToString(),
            p.Version,
            p.EngineVersion,
            p.Name,
            p.Description,
            p.Expression,
            p.PassScorePct,
            p.SourceTemplateId,
            p.CreatedAt,
            p.CreatedBy);
    }
}
