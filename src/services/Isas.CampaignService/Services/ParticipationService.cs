using Isas.CampaignService.DTOs;
using Isas.CampaignService.Models;
using Microsoft.EntityFrameworkCore;

namespace Isas.CampaignService.Services
{
    /// <summary>
    /// D2 — orchestrator luồng ứng viên: xem lời mời → tham gia (join) → membership → my-campaigns →
    /// bắt đầu phỏng vấn. Provision Candidate qua Auth (INT-13/D8), tạo session qua Interview (I1).
    /// KHÔNG FK xuyên service (candidateId/sessionId = Guid lỏng).
    /// </summary>
    public class ParticipationService : IParticipationService
    {
        private readonly CampaignDbContext _db;
        private readonly IAuthProvisionClient _authClient;
        private readonly ICampaignSessionClient _sessionClient;
        private readonly ILogger<ParticipationService> _logger;

        public ParticipationService(
            CampaignDbContext db,
            IAuthProvisionClient authClient,
            ICampaignSessionClient sessionClient,
            ILogger<ParticipationService> logger)
        {
            _db = db;
            _authClient = authClient;
            _sessionClient = sessionClient;
            _logger = logger;
        }

        // ── GET /invitations/{token} — metadata (KHÔNG side-effect) ──────────────────
        public async Task<InvitationMetadataResponse> GetInvitationMetadataAsync(string token, CancellationToken ct = default)
        {
            var inv = await _db.CampaignInvitations
                .AsNoTracking()
                .Include(i => i.Campaign).ThenInclude(c => c.Criteria)
                .FirstOrDefaultAsync(i => i.Token == token, ct)
                ?? throw new KeyNotFoundException("Lời mời không tồn tại.");

            ValidateInvitationUsable(inv);

            return new InvitationMetadataResponse
            {
                CampaignId = inv.CampaignId,
                Title = inv.Campaign.Title,
                OrgName = null,   // Campaign chỉ có org_id — tên org phải resolve qua Auth (ngoài phạm vi D2)
                JobTitle = inv.Campaign.Domain,
                Description = inv.Campaign.JDText,
                Deadline = inv.Campaign.ExpiresAt,
                Criteria = inv.Campaign.Criteria.OrderBy(c => c.OrderNo).Select(MapCriterion).ToList()
            };
        }

        // ── POST /invitations/{token}/join — tham gia campaign ───────────────────────
        public async Task<JoinCampaignResponse> JoinCampaignAsync(string token, CancellationToken ct = default)
        {
            var inv = await _db.CampaignInvitations
                .Include(i => i.Campaign)
                .FirstOrDefaultAsync(i => i.Token == token, ct)
                ?? throw new KeyNotFoundException("Lời mời không tồn tại.");

            ValidateInvitationUsable(inv);

            // Provision account Candidate nhẹ theo email của lời mời (idempotent bên Auth).
            var provisioned = await _authClient.ProvisionCandidateAsync(inv.Email, null, ct);
            var candidateId = provisioned.CandidateId;
            var now = DateTime.UtcNow;

            var membership = await ResolveMembershipAsync(inv, candidateId, ct);
            if (membership is null)
            {
                membership = new CampaignCandidate
                {
                    Id = Guid.NewGuid(),
                    CampaignId = inv.CampaignId,
                    CandidateId = candidateId,
                    Email = inv.Email,
                    ParseStatus = CvParseStatus.Pending,   // đường 1 (email) — không có CV
                    Status = CandidateStatus.Joined,
                    JoinedAt = now,
                    CreatedAt = now,
                    UpdatedAt = now
                };
                _db.CampaignCandidates.Add(membership);
            }
            else
            {
                // Idempotent: gắn candidate + đánh Joined (không hạ cấp / không tạo row thứ 2).
                membership.CandidateId = candidateId;
                if (membership.Status != CandidateStatus.Joined)
                    membership.Status = CandidateStatus.Joined;
                membership.JoinedAt ??= now;
                membership.UpdatedAt = now;
            }

            await _db.SaveChangesAsync(ct);

            _logger.LogInformation(
                "D2 join: candidate {CandidateId} tham gia campaign {CampaignId} (membership {MembershipId})",
                candidateId, inv.CampaignId, membership.Id);

            return new JoinCampaignResponse
            {
                AccessToken = provisioned.AccessToken,
                CampaignId = inv.CampaignId,
                CandidateId = candidateId,
                MembershipStatus = CandidateStatus.Joined.ToString()
            };
        }

        // ── GET /my-campaigns ────────────────────────────────────────────────────────
        public async Task<List<MyCampaignItem>> GetMyCampaignsAsync(Guid candidateId, CancellationToken ct = default)
        {
            var rows = await _db.CampaignCandidates
                .AsNoTracking()
                .Include(c => c.Campaign)
                .Where(c => c.CandidateId == candidateId)
                .OrderByDescending(c => c.JoinedAt)
                .ToListAsync(ct);

            // Campaign soft-delete (query filter) → nav null → bỏ (không hiện campaign đã xoá).
            return rows.Where(c => c.Campaign is not null).Select(c => new MyCampaignItem
            {
                CampaignId = c.CampaignId,
                Title = c.Campaign.Title,
                Company = null,
                JobTitle = c.Campaign.Domain,
                Deadline = c.Campaign.ExpiresAt,
                MembershipStatus = c.Status.ToString(),
                InterviewStatus = (c.InterviewStatus ?? InterviewProgressStatus.NotStarted).ToString()
            }).ToList();
        }

        // ── GET /my-campaigns/{id} — chi tiết cho ứng viên đã join ────────────────────
        public async Task<CandidateCampaignDetailResponse> GetCandidateCampaignAsync(
            Guid candidateId, Guid campaignId, CancellationToken ct = default)
        {
            var membership = await _db.CampaignCandidates
                .AsNoTracking()
                .Include(c => c.Campaign).ThenInclude(c => c.Criteria)
                .FirstOrDefaultAsync(c => c.CampaignId == campaignId && c.CandidateId == candidateId, ct);

            if (membership is null || membership.Campaign is null)
                throw new KeyNotFoundException("Bạn không phải thành viên của chiến dịch này.");

            var interviewStatus = membership.InterviewStatus ?? InterviewProgressStatus.NotStarted;

            return new CandidateCampaignDetailResponse
            {
                CampaignId = campaignId,
                Title = membership.Campaign.Title,
                JobTitle = membership.Campaign.Domain,
                Description = membership.Campaign.JDText,
                Deadline = membership.Campaign.ExpiresAt,
                Criteria = membership.Campaign.Criteria.OrderBy(c => c.OrderNo).Select(MapCriterion).ToList(),
                MembershipStatus = membership.Status.ToString(),
                InterviewStatus = interviewStatus.ToString(),
                SessionId = membership.SessionId,
                Started = membership.SessionId is not null || interviewStatus != InterviewProgressStatus.NotStarted
            };
        }

        // ── POST /campaign/{id}/start — bắt đầu phỏng vấn (create-or-get session) ──────
        public async Task<StartInterviewResponse> StartInterviewAsync(
            Guid candidateId, Guid campaignId, CancellationToken ct = default)
        {
            var membership = await _db.CampaignCandidates
                .Include(c => c.Campaign).ThenInclude(c => c.Questions)
                .Include(c => c.Campaign).ThenInclude(c => c.Criteria)
                .FirstOrDefaultAsync(c => c.CampaignId == campaignId && c.CandidateId == candidateId, ct);

            // Chưa join (không có membership gắn candidate) → 403 (UnauthorizedAccessException).
            if (membership is null || membership.Campaign is null)
                throw new UnauthorizedAccessException("Bạn cần tham gia chiến dịch trước khi bắt đầu phỏng vấn.");

            var campaign = membership.Campaign;

            // Campaign còn cho phỏng vấn: Active + chưa hết hạn (→ 409 nếu không).
            if (campaign.Status != CampaignStatus.Active)
                throw new InvalidOperationException($"Chiến dịch không còn cho phỏng vấn (trạng thái {campaign.Status}).");
            if (campaign.ExpiresAt is DateTime exp && exp < DateTime.UtcNow)
                throw new InvalidOperationException("Chiến dịch đã hết hạn phỏng vấn.");

            // Đã hoàn thành → không cho làm lại (biên idempotency phía membership).
            if (membership.InterviewStatus == InterviewProgressStatus.Completed)
                throw new InvalidOperationException("Bạn đã hoàn thành phỏng vấn của chiến dịch này.");

            var questions = campaign.Questions
                .OrderBy(q => q.CreatedAt).ThenBy(q => q.Id)
                .Select(q => q.QuestionText)
                .ToList();
            if (questions.Count == 0)
                throw new InvalidOperationException("Chiến dịch chưa có câu hỏi.");

            var criteria = campaign.Criteria
                .OrderBy(c => c.OrderNo)
                .Select(c => new SessionCriterionInput(c.Name, c.Description, c.Weight, c.MaxScore))
                .ToList();
            if (criteria.Count == 0)
                throw new InvalidOperationException("Chiến dịch chưa có tiêu chí chấm.");

            var jobCategory = string.IsNullOrWhiteSpace(campaign.Domain) ? "BE" : campaign.Domain!;

            // Create-or-get session (Interview dedup theo candidate+campaign) → bấm nhiều lần vẫn ra CÙNG session.
            // BK18 — gửi kèm campaign.ExpiresAt → Interview set session.Deadline (I2) cho sweeper auto-submit/abandon.
            var session = await _sessionClient.CreateOrGetSessionAsync(
                candidateId, campaignId, jobCategory, questions, criteria, campaign.ExpiresAt, ct);

            membership.SessionId = session.SessionId;
            if (membership.InterviewStatus is null or InterviewProgressStatus.NotStarted)
                membership.InterviewStatus = InterviewProgressStatus.InProgress;
            membership.UpdatedAt = DateTime.UtcNow;
            await _db.SaveChangesAsync(ct);

            _logger.LogInformation(
                "D2 start: candidate {CandidateId} bắt đầu phỏng vấn campaign {CampaignId} → session {SessionId}",
                candidateId, campaignId, session.SessionId);

            return new StartInterviewResponse
            {
                SessionId = session.SessionId,
                CampaignId = campaignId,
                Questions = session.Questions
                    .OrderBy(q => q.OrderNo)
                    .Select(q => new StartQuestionItem
                    {
                        Id = q.Id,
                        OrderNo = q.OrderNo,
                        Content = q.Content,
                        TimeLimitSec = q.TimeLimitSec
                    }).ToList()
            };
        }

        // ── helpers ──────────────────────────────────────────────────────────────────

        // Tìm membership để cập nhật khi join. Đường 2 (CV shortlist): dùng campaign_candidate_id gắn sẵn.
        // Đường 1 (email) hoặc fallback: dedup theo (campaign, candidate) rồi (campaign, email) — chống tạo trùng
        // & chống đụng UNIQUE(campaign_id, email).
        private async Task<CampaignCandidate?> ResolveMembershipAsync(
            CampaignInvitation inv, Guid candidateId, CancellationToken ct)
        {
            if (inv.CampaignCandidateId is Guid ccid)
            {
                var linked = await _db.CampaignCandidates
                    .FirstOrDefaultAsync(c => c.Id == ccid && c.CampaignId == inv.CampaignId, ct);
                if (linked is not null)
                    return linked;
            }

            var byCandidate = await _db.CampaignCandidates
                .FirstOrDefaultAsync(c => c.CampaignId == inv.CampaignId && c.CandidateId == candidateId, ct);
            if (byCandidate is not null)
                return byCandidate;

            var email = inv.Email.ToLower();
            return await _db.CampaignCandidates
                .FirstOrDefaultAsync(c => c.CampaignId == inv.CampaignId
                    && c.Email != null && c.Email.ToLower() == email, ct);
        }

        // Lời mời còn dùng được: chưa revoke, chưa hết hạn, campaign còn Active. Ngược lại → 410 Gone.
        private static void ValidateInvitationUsable(CampaignInvitation inv)
        {
            if (inv.Campaign is null)
                throw new InvitationGoneException("Chiến dịch không còn khả dụng.");
            if (inv.RevokedAt is not null)
                throw new InvitationGoneException("Lời mời đã bị thu hồi.");
            if (inv.ExpiresAt is DateTime exp && exp < DateTime.UtcNow)
                throw new InvitationGoneException("Lời mời đã hết hạn.");
            if (inv.Campaign.Status != CampaignStatus.Active)
                throw new InvitationGoneException($"Chiến dịch không còn nhận ứng viên (trạng thái {inv.Campaign.Status}).");
        }

        private static CampaignCriterionResponse MapCriterion(CampaignCriterion c) => new()
        {
            Id = c.Id,
            OrderNo = c.OrderNo,
            Name = c.Name,
            Description = c.Description,
            Weight = c.Weight,
            MaxScore = c.MaxScore,
            Source = c.Source.ToString()
        };
    }
}
