using Isas.InterviewService.ApplicationDbContext;
using Isas.InterviewService.DTOs;
using Isas.InterviewService.Enums;
using Isas.InterviewService.Services.Interfaces;
using Microsoft.EntityFrameworkCore;

namespace Isas.InterviewService.Services;

public class SessionScoringNotifier : ISessionScoringNotifier
{
    private readonly InterviewDbContext _db;
    private readonly ISessionEventPublisher _eventPublisher;
    private readonly ILogger<SessionScoringNotifier> _logger;

    public SessionScoringNotifier(
        InterviewDbContext db,
        ISessionEventPublisher eventPublisher,
        ILogger<SessionScoringNotifier> logger)
    {
        _db = db;
        _eventPublisher = eventPublisher;
        _logger = logger;
    }

    public async Task NotifySessionScoredAsync(Guid sessionId, CancellationToken ct = default)
    {
        var session = await _db.PracticeSessions
            .AsNoTracking()
            .FirstOrDefaultAsync(s => s.Id == sessionId, ct);
        if (session is null) return;

        var totalScore = await ComputeWeightedTotalScoreAsync(
            session.Id, session.CampaignId, session.JobCategory, ct);

        var evt = new SessionScoredEvent
        {
            SessionId = session.Id,
            CampaignId = session.CampaignId,
            CandidateId = session.CandidateId,
            TotalScore = totalScore,
            ScoredAt = DateTime.UtcNow
        };

        try
        {
            await _eventPublisher.PublishSessionScoredAsync(evt, ct);
            _logger.LogInformation(
                "Phát SessionScored: session={SessionId} campaign={CampaignId} score={Score}",
                session.Id, session.CampaignId, totalScore);
        }
        catch (Exception ex)
        {
            // Publish lỗi KHÔNG được làm hỏng việc đóng session — session đã Scored trong DB
            // rồi (giống pattern nuốt lỗi publish ở AnswerService.TryPublishScoringJobAsync).
            // Miss event ở đây tạm thời làm Campaign/Payment lệch (chưa có backfill trong E2).
            _logger.LogError(ex, "Phát SessionScored thất bại cho session {SessionId}", sessionId);
        }
    }

    // Điểm tổng có trọng số dùng cho event (ranking B2B — campaign.md §campaign_rankings:
    // "total_score = Σ pct×weight, chuẩn hoá chia Σweight — Interview tính"). Áp dụng chung cho
    // cả B2C: đây chỉ là điểm SNAPSHOT phát kèm event, KHÔNG phải overallScore BC9 (equal-weight,
    // ghi DB) — BC9 là task riêng (not_started), không thuộc phạm vi E2.
    private async Task<decimal> ComputeWeightedTotalScoreAsync(
        Guid sessionId, Guid? campaignId, JobCategory jobCategory, CancellationToken ct)
    {
        // Nguồn tiêu chí tùy mode (E1, giống AnswerService.TryPublishScoringJobAsync):
        // B2B theo campaign_id, B2C theo job_category + campaign_id IS NULL.
        var criteriaQuery = _db.RubricCriteria.AsNoTracking().Where(c => c.IsActive);
        criteriaQuery = campaignId is Guid cid
            ? criteriaQuery.Where(c => c.CampaignId == cid)
            : criteriaQuery.Where(c => c.CampaignId == null && c.JobCategory == jobCategory);
        var criteria = await criteriaQuery.ToListAsync(ct);
        if (criteria.Count == 0) return 0m;

        var scores = await _db.AnswerScores
            .AsNoTracking()
            .Where(sc => sc.Answer.SessionId == sessionId)
            .Select(sc => new { sc.AnswerId, sc.CriterionId, sc.AttemptNo, sc.Score })
            .ToListAsync(ct);
        if (scores.Count == 0) return 0m;

        // Mỗi (answer, criterion) lấy attempt mới nhất — cùng cách hiển thị hiện tại
        // (PracticeService.MapAnswer), self-consistency (nhiều attempt) chưa build.
        var latestPerAnswerCriterion = scores
            .GroupBy(s => (s.AnswerId, s.CriterionId))
            .Select(g => g.OrderByDescending(s => s.AttemptNo).First());

        // Điểm TB mỗi tiêu chí qua các answer đã chấm (BC9 §Công thức bước 1, tái dùng cho B2B).
        var avgByCriterion = latestPerAnswerCriterion
            .GroupBy(s => s.CriterionId)
            .ToDictionary(g => g.Key, g => g.Average(s => s.Score));

        // maxScore khác nhau giữa các tiêu chí ⇒ không cộng điểm thô — chuẩn theo % trước khi
        // gộp trọng số (interview.md §Đánh giá cách chấm tiêu chí #2).
        decimal weightedSum = 0m;
        decimal weightSum = 0m;
        foreach (var c in criteria)
        {
            if (!avgByCriterion.TryGetValue(c.Id, out var avgScore)) continue;
            var maxScore = c.MaxScore > 0 ? c.MaxScore : 1; // phòng chia 0 (ràng buộc maxScore≥1)
            var pct = Math.Clamp(avgScore / maxScore * 100m, 0m, 100m);
            weightedSum += pct * c.Weight;
            weightSum += c.Weight;
        }

        if (weightSum <= 0m) return 0m;
        return Math.Clamp(Math.Round(weightedSum / weightSum, 2), 0m, 100m);
    }
}
