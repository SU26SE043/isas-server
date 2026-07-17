using Isas.CampaignService.DTOs;
using Isas.CampaignService.Models;
using Microsoft.EntityFrameworkCore;
using System.ComponentModel.DataAnnotations;

namespace Isas.CampaignService.Services
{
    /// <summary>
    /// C14 — Sàng CV B2B async (D18/D19). Publish job AI chấm khớp cho ứng viên <c>Filtered</c>,
    /// nhận callback ghi điểm/tiêu chí (idempotent, chống ảo giác), shortlist + PATCH email/fullName.
    /// TÁI DÙNG <c>campaign_criteria</c> làm rubric; KHÔNG trừ credit (D19). Tách khỏi
    /// <see cref="CampaignService"/> để giữ nguyên constructor service C13.
    /// </summary>
    public class CvScreeningService : ICvScreeningService
    {
        private readonly CampaignDbContext _db;
        private readonly ICvScreeningPublisher _publisher;
        private readonly IConfiguration _config;
        private readonly ILogger<CvScreeningService> _logger;

        public CvScreeningService(
            CampaignDbContext db,
            ICvScreeningPublisher publisher,
            IConfiguration config,
            ILogger<CvScreeningService> logger)
        {
            _db = db;
            _publisher = publisher;
            _config = config;
            _logger = logger;
        }

        // ── Publish job sàng cho các ứng viên Filtered → Analyzing ──────────────────────────
        // Best-effort per-candidate: publish hụt → giữ Filtered (last_screening_published_at=null) →
        // C15 StuckScreeningRepublisher đẩy lại. TÁI DÙNG campaign_criteria làm rubric gửi kèm job.
        public async Task<int> PublishScreeningJobsAsync(Guid orgId, Guid campaignId, CancellationToken ct)
        {
            var campaign = await _db.Campaigns
                .Include(c => c.Criteria)
                .FirstOrDefaultAsync(c => c.Id == campaignId && c.OrgId == orgId, ct)
                ?? throw new KeyNotFoundException($"Campaign {campaignId} not found.");

            var candidates = await _db.CvSubmissions
                .Where(c => c.CampaignId == campaignId && c.Status == CvSubmissionStatus.Filtered)
                .ToListAsync(ct);

            if (candidates.Count == 0)
                return 0;

            var criteria = campaign.Criteria
                .OrderBy(c => c.OrderNo)
                .Select(c => new CvScreeningCriterion(c.Id, c.Name, c.Description, c.MaxScore))
                .ToList();

            // callbackBase đi kèm job vì worker mặc định trỏ Interview — B2B phải trỏ CampaignService (ai.md).
            var callbackBase = _config["Internal:CallbackBase"] ?? "http://localhost:8080";
            var now = DateTime.UtcNow;
            int published = 0;

            foreach (var cand in candidates)
            {
                try
                {
                    await _publisher.PublishAsync(new CvScreeningJob(
                        cand.Id,
                        cand.CvParsedText ?? string.Empty,
                        campaign.Domain,
                        campaign.JDText,
                        criteria,
                        callbackBase), ct);

                    cand.Status = CvSubmissionStatus.Analyzing;
                    cand.LastScreeningPublishedAt = now;
                    cand.UpdatedAt = now;
                    published++;
                }
                catch (Exception ex)
                {
                    // Giữ Filtered để C15 republisher đẩy lại — KHÔNG chặn cả batch vì 1 CV publish hụt.
                    _logger.LogError(ex, "Publish cv_screening_queue thất bại cho candidate {CandidateId}", cand.Id);
                }
            }

            if (published > 0)
                await _db.SaveChangesAsync(ct);

            return published;
        }

        // ── Callback cv-result → ghi điểm + Analyzed (idempotent, chống ảo giác, recover ngoài thứ tự) ──
        public async Task<CvResultOutcome> SaveCvResultAsync(Guid candidateId, CvResultCallbackRequest req, CancellationToken ct)
        {
            var candidate = await _db.CvSubmissions
                .FirstOrDefaultAsync(c => c.Id == candidateId, ct)
                ?? throw new KeyNotFoundException($"Candidate {candidateId} not found.");

            // Callback MUỘN sau khi HR đã mời (Invited) → bỏ qua (không lật kết quả đã chốt).
            if (candidate.Status == CvSubmissionStatus.Invited)
            {
                _logger.LogInformation("cv-result cho candidate {CandidateId} bị bỏ qua: đã Invited.", candidateId);
                return CvResultOutcome.SkippedInvited;
            }

            // Chống ảo giác: chỉ nhận criterion_id có THẬT trong campaign_criteria của campaign này.
            var criteria = await _db.CampaignCriteria
                .Where(c => c.CampaignId == candidate.CampaignId)
                .ToDictionaryAsync(c => c.Id, ct);

            // Idempotent: xoá điểm cũ rồi ghi lại → callback 2 lần KHÔNG nhân đôi (EF xếp DELETE trước
            // INSERT trong 1 SaveChanges dù trùng UNIQUE(candidate_id, criterion_id) — như replace-all C12).
            var existingScores = await _db.CandidateCriterionScores
                .Where(s => s.CandidateId == candidateId)
                .ToListAsync(ct);
            _db.CandidateCriterionScores.RemoveRange(existingScores);

            var now = DateTime.UtcNow;
            foreach (var m in req.CriterionMatches ?? new List<CriterionMatchItem>())
            {
                if (!criteria.TryGetValue(m.CriterionId, out var crit))
                    continue;   // bỏ criterion_id AI bịa (FK Restrict cũng chặn, lọc sớm cho sạch)

                _db.CandidateCriterionScores.Add(new CandidateCriterionScore
                {
                    Id = Guid.NewGuid(),
                    CandidateId = candidateId,
                    CriterionId = m.CriterionId,
                    MatchScore = Math.Clamp(m.MatchScore, 0m, crit.MaxScore),   // kẹp [0, max_score] (INT-9)
                    Reasoning = m.Reasoning,
                    CreatedAt = now
                });
            }

            candidate.Skills = req.Skills;
            candidate.YearsExperience = req.YearsExperience;
            candidate.Summary = req.Summary;
            candidate.OverallMatchScore = Math.Clamp(req.OverallMatchScore, 0, 100);
            candidate.RejectReason = null;   // xoá lý do AnalysisFailed cũ khi recover (retry thành công)
            candidate.Status = CvSubmissionStatus.Analyzed;   // recover cả từ Analyzing lẫn AnalysisFailed (doc)
            candidate.UpdatedAt = now;

            await _db.SaveChangesAsync(ct);
            return CvResultOutcome.Analyzed;
        }

        // ── Callback cv-failed → AnalysisFailed (absorbing: đã Analyzed/Invited → no-op) ────────────
        public async Task<CvFailedOutcome> MarkCvFailedAsync(Guid candidateId, string? reason, CancellationToken ct)
        {
            var candidate = await _db.CvSubmissions
                .FirstOrDefaultAsync(c => c.Id == candidateId, ct)
                ?? throw new KeyNotFoundException($"Candidate {candidateId} not found.");

            // Đã Invited → bỏ qua (không lật trạng thái sau khi mời).
            if (candidate.Status == CvSubmissionStatus.Invited)
                return CvFailedOutcome.SkippedInvited;

            // Đã Analyzed (kết quả tốt về trước) → KHÔNG để cv-failed muộn xoá kết quả.
            if (candidate.Status == CvSubmissionStatus.Analyzed)
                return CvFailedOutcome.SkippedAnalyzed;

            candidate.Status = CvSubmissionStatus.AnalysisFailed;
            candidate.RejectReason = string.IsNullOrWhiteSpace(reason) ? "Phân tích CV thất bại." : reason.Trim();
            candidate.UpdatedAt = DateTime.UtcNow;
            await _db.SaveChangesAsync(ct);
            return CvFailedOutcome.Failed;
        }

        // ── Shortlist: sort=score DESC (mặc định) — ranking derived overall_match_score ─────────────
        public async Task<List<CandidateListItem>> GetCandidatesAsync(
            Guid orgId, Guid campaignId, string? status, int? minScore, string? skill, string? sort, CancellationToken ct)
        {
            // Ownership: campaign phải của org (query filter loại soft-deleted) → không thấy = 404.
            var owns = await _db.Campaigns.AnyAsync(c => c.Id == campaignId && c.OrgId == orgId, ct);
            if (!owns)
                throw new KeyNotFoundException($"Campaign {campaignId} not found.");

            var q = _db.CvSubmissions.Where(c => c.CampaignId == campaignId);

            if (!string.IsNullOrWhiteSpace(status) &&
                Enum.TryParse<CvSubmissionStatus>(status, ignoreCase: true, out var st))
                q = q.Where(c => c.Status == st);

            if (minScore is int min)
                q = q.Where(c => c.OverallMatchScore != null && c.OverallMatchScore >= min);

            var rows = await q.ToListAsync(ct);

            // skill: Skills là jsonb string[] → lọc trong C# (không query trong JSON — portable Npgsql/SQLite).
            if (!string.IsNullOrWhiteSpace(skill))
            {
                var needle = skill.Trim();
                rows = rows.Where(c => c.Skills != null &&
                    c.Skills.Any(s => s.Contains(needle, StringComparison.OrdinalIgnoreCase))).ToList();
            }

            // sort in-memory (N ≤ max_candidates): mặc định score DESC, null (chưa Analyzed) xuống cuối; "name".
            var normalizedSort = string.IsNullOrWhiteSpace(sort) ? "score" : sort.Trim().ToLowerInvariant();
            rows = normalizedSort == "name"
                ? rows.OrderBy(c => c.FullName ?? string.Empty, StringComparer.OrdinalIgnoreCase)
                      .ThenBy(c => c.CreatedAt).ToList()
                : rows.OrderByDescending(c => c.OverallMatchScore ?? int.MinValue)
                      .ThenBy(c => c.CreatedAt).ToList();

            return rows.Select(c => new CandidateListItem
            {
                Id = c.Id,
                FullName = c.FullName,
                Email = c.Email,
                Status = c.Status.ToString(),
                OverallMatchScore = c.OverallMatchScore,
                Skills = c.Skills
            }).ToList();
        }

        // ── Chi tiết 1 ứng viên + điểm từng tiêu chí (reasoning) ─────────────────────────────────────
        public async Task<CandidateDetailResponse> GetCandidateAsync(
            Guid orgId, Guid campaignId, Guid candidateId, CancellationToken ct)
        {
            var candidate = await _db.CvSubmissions
                .FirstOrDefaultAsync(
                    c => c.Id == candidateId && c.CampaignId == campaignId && c.Campaign.OrgId == orgId, ct)
                ?? throw new KeyNotFoundException($"Candidate {candidateId} not found.");

            var scores = await (from s in _db.CandidateCriterionScores
                                join cr in _db.CampaignCriteria on s.CriterionId equals cr.Id
                                where s.CandidateId == candidateId
                                orderby cr.OrderNo
                                select new CriterionScoreItem
                                {
                                    CriterionId = s.CriterionId,
                                    CriterionName = cr.Name,
                                    MatchScore = s.MatchScore,
                                    MaxScore = cr.MaxScore,
                                    Reasoning = s.Reasoning
                                }).ToListAsync(ct);

            return new CandidateDetailResponse
            {
                Id = candidate.Id,
                FullName = candidate.FullName,
                Email = candidate.Email,
                Status = candidate.Status.ToString(),
                OverallMatchScore = candidate.OverallMatchScore,
                Skills = candidate.Skills,
                YearsExperience = candidate.YearsExperience,
                Summary = candidate.Summary,
                RejectReason = candidate.RejectReason,
                CvFileUrl = candidate.CvFileUrl,
                CriterionScores = scores
            };
        }

        // ── PATCH email/fullName (parse thiếu) → audit_logs; đã Invited → 409; trùng email → 400 ──────
        public async Task PatchCandidateAsync(
            Guid orgId, Guid actorUserId, Guid campaignId, Guid candidateId, PatchCandidateRequest req, CancellationToken ct)
        {
            var candidate = await _db.CvSubmissions
                .FirstOrDefaultAsync(
                    c => c.Id == candidateId && c.CampaignId == campaignId && c.Campaign.OrgId == orgId, ct)
                ?? throw new KeyNotFoundException($"Candidate {candidateId} not found.");

            // Đã phát magic-link (email đã dùng) → khoá sửa → 409.
            if (candidate.Status == CvSubmissionStatus.Invited)
                throw new InvalidOperationException("Không sửa được ứng viên đã Invited.");

            var changed = new List<string>();

            if (req.Email is not null)
            {
                var email = req.Email.Trim();
                if (email.Length == 0)
                    throw new ArgumentException("email không được rỗng.");
                if (!new EmailAddressAttribute().IsValid(email))
                    throw new ArgumentException("Định dạng email không hợp lệ.");
                email = email.ToLowerInvariant();   // dedup/lưu chuẩn hoá lowercase (như C13)

                // UNIQUE(campaign_id, email): trùng ứng viên KHÁC trong campaign → 400 (chặn trước DB).
                var dup = await _db.CvSubmissions.AnyAsync(
                    c => c.CampaignId == campaignId && c.Id != candidateId && c.Email == email, ct);
                if (dup)
                    throw new ArgumentException($"Email '{email}' đã tồn tại trong campaign.");

                candidate.Email = email;
                changed.Add("email");
            }

            if (req.FullName is not null)
            {
                candidate.FullName = string.IsNullOrWhiteSpace(req.FullName) ? null : req.FullName.Trim();
                changed.Add("fullName");
            }

            if (changed.Count == 0)
                throw new ArgumentException("Cần ít nhất 1 trường (email/fullName) để cập nhật.");

            candidate.UpdatedAt = DateTime.UtcNow;
            _db.AuditLogs.Add(new AuditLog
            {
                Id = Guid.NewGuid(),
                OrgId = orgId,                  // BK4: ORG sở hữu campaign (ownership context)
                ActorUserId = actorUserId,      // cá nhân HR thao tác (user sub, không phải org)
                Action = AuditAction.EditCandidate,
                Entity = "CvSubmission",
                EntityId = candidateId,
                Summary = $"Sửa {string.Join("/", changed)} ứng viên sàng CV",
                At = DateTime.UtcNow
            });
            await _db.SaveChangesAsync(ct);
        }
    }
}
