using Isas.CampaignService.DTOs;
using Isas.CampaignService.Models;
using Microsoft.EntityFrameworkCore;

namespace Isas.CampaignService.Services
{
    /// <summary>
    /// E4: nghe event <c>SessionScored</c> (do SessionScoredConsumer đẩy vào sau khi tiêu thụ
    /// RabbitMQ) → upsert <c>campaign_rankings</c> theo <c>session_id</c> (idempotent — D10).
    /// Chỉ xếp hạng B2B (<c>campaign_id</c> có giá trị); B2C (<c>campaign_id=null</c>) bị bỏ qua,
    /// không tạo row. <c>TotalScore</c> là snapshot Interview đã tính có trọng số — lưu nguyên,
    /// KHÔNG recompute ở đây.
    /// </summary>
    public class RankingEventHandler : IRankingEventHandler
    {
        private readonly CampaignDbContext _db;
        private readonly ILogger<RankingEventHandler> _logger;

        public RankingEventHandler(CampaignDbContext db, ILogger<RankingEventHandler> logger)
        {
            _db = db;
            _logger = logger;
        }

        public async Task HandleSessionScoredAsync(SessionScoredMessage evt, CancellationToken ct = default)
        {
            if (evt.CampaignId is null)
            {
                _logger.LogInformation(
                    "SessionScored session {SessionId} là B2C (campaign_id=null) — E4 chỉ xếp hạng B2B, bỏ qua.",
                    evt.SessionId);
                return;
            }

            var existing = await _db.CampaignRankings
                .FirstOrDefaultAsync(x => x.SessionId == evt.SessionId, ct);

            if (existing is null)
            {
                _db.CampaignRankings.Add(new CampaignRanking
                {
                    Id = Guid.NewGuid(),
                    CampaignId = evt.CampaignId.Value,
                    CandidateId = evt.CandidateId,
                    SessionId = evt.SessionId,
                    TotalScore = evt.TotalScore,
                    UpdatedAt = DateTime.UtcNow
                });

                _logger.LogInformation(
                    "campaign_rankings: tạo mới cho session {SessionId} (campaign={CampaignId}, score={Score})",
                    evt.SessionId, evt.CampaignId, evt.TotalScore);
            }
            else
            {
                // Idempotent upsert: event tới lần nữa (redelivery/duplicate publish) cho CÙNG
                // session_id → cập nhật tại chỗ, KHÔNG tạo thêm row (UNIQUE(session_id) chặn ở DB
                // nếu có race, nhưng luồng bình thường xử lý tuần tự nên find-or-update là đủ).
                existing.CampaignId = evt.CampaignId.Value;
                existing.CandidateId = evt.CandidateId;
                existing.TotalScore = evt.TotalScore;
                existing.UpdatedAt = DateTime.UtcNow;

                _logger.LogInformation(
                    "campaign_rankings: upsert (đã có) cho session {SessionId} (campaign={CampaignId}, score={Score})",
                    evt.SessionId, evt.CampaignId, evt.TotalScore);
            }

            // D2: đánh dấu membership hoàn thành phỏng vấn (interview_status = Completed) — cùng transaction.
            await MarkMembershipCompletedAsync(evt, ct);

            await _db.SaveChangesAsync(ct);
        }

        public async Task HandleSessionAbandonedAsync(SessionAbandonedMessage evt, CancellationToken ct = default)
        {
            if (evt.CampaignId is null)
                return;

            var membership = await _db.CampaignMemberships
                .FirstOrDefaultAsync(m => m.SessionId == evt.SessionId, ct);
            if (membership is null)
                membership = await _db.CampaignMemberships.FirstOrDefaultAsync(
                    m => m.CampaignId == evt.CampaignId && m.CandidateId == evt.CandidateId, ct);

            // Absorbing Completed: delayed abandon events must never erase a scored result.
            if (membership is null || membership.InterviewStatus == InterviewProgressStatus.Completed)
                return;

            membership.InterviewStatus = InterviewProgressStatus.Abandoned;
            membership.SessionId ??= evt.SessionId;
            membership.InterviewDeadlineAt = null;
            membership.UpdatedAt = DateTime.UtcNow;
            await _db.SaveChangesAsync(ct);
            _logger.LogInformation("membership {MembershipId} released after abandoned session {SessionId}", membership.Id, evt.SessionId);
        }

        // D2/DB16: session Scored → membership (campaign_membership) interview_status = Completed. Match theo
        // session_id (chắc chắn đúng membership) rồi fallback (campaign, candidate). Idempotent (đã Completed
        // → no-op). Không có membership (luồng không qua D2) → no-op, KHÔNG phá ranking.
        private async Task MarkMembershipCompletedAsync(SessionScoredMessage evt, CancellationToken ct)
        {
            var membership = await _db.CampaignMemberships
                .FirstOrDefaultAsync(m => m.SessionId == evt.SessionId, ct);

            if (membership is null && evt.CampaignId is Guid campId)
                membership = await _db.CampaignMemberships
                    .FirstOrDefaultAsync(m => m.CampaignId == campId && m.CandidateId == evt.CandidateId, ct);

            if (membership is null || membership.InterviewStatus == InterviewProgressStatus.Completed)
                return;

            membership.InterviewStatus = InterviewProgressStatus.Completed;
            membership.SessionId ??= evt.SessionId;
            membership.UpdatedAt = DateTime.UtcNow;

            _logger.LogInformation(
                "D2: membership {MembershipId} (candidate {CandidateId}, campaign {CampaignId}) → Completed",
                membership.Id, evt.CandidateId, evt.CampaignId);
        }
    }
}
