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
                .Select(p => new ScoringPolicyResponse(
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
                p.CreatedBy)).ToList();
        }
    }
}
