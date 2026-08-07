using Isas.InterviewService.ApplicationDbContext;
using Isas.InterviewService.DTOs;
using Isas.InterviewService.Entities;
using Isas.InterviewService.Enums;
using Isas.InterviewService.Services.Interfaces;
using Microsoft.EntityFrameworkCore;

namespace Isas.InterviewService.Services;

// DB2 — Transactional Outbox: nơi đóng session GHI outbox-row (settlement-event) CÙNG transaction với
// state-flip, thay cho "publish best-effort SAU SaveChanges" cũ (mất event khi broker chết). OutboxDispatcher
// publish row lên "interview.events". Notifier KHÔNG còn publish/không giữ ISessionEventPublisher.
public class SessionScoringNotifier : ISessionScoringNotifier
{
    private readonly InterviewDbContext _db;
    private readonly ISessionResultService _resultService;
    private readonly IAiServiceSessionSummarizer _summarizer;   // BC10
    private readonly IRoadmapReportService _roadmapReport;      // BC15
    private readonly ILogger<SessionScoringNotifier> _logger;

    public SessionScoringNotifier(
        InterviewDbContext db,
        ISessionResultService resultService,
        IAiServiceSessionSummarizer summarizer,
        IRoadmapReportService roadmapReport,
        ILogger<SessionScoringNotifier> logger)
    {
        _db = db;
        _resultService = resultService;
        _summarizer = summarizer;
        _roadmapReport = roadmapReport;
        _logger = logger;
    }

    // Tính điểm tổng (weighted) + build SessionScoredEvent + `db.OutboxMessages.Add(row)` — KHÔNG save
    // (caller commit chung với state-flip → đóng session state & outbox-row cùng 1 transaction, atomic).
    // Không phụ thuộc BC9 (điểm event = weighted tính từ answer_scores) nên gọi được TRƯỚC SaveChanges.
    public async Task EnqueueSessionScoredAsync(Guid sessionId, CancellationToken ct = default)
    {
        var session = await _db.PracticeSessions
            .AsNoTracking()
            .FirstOrDefaultAsync(s => s.Id == sessionId, ct);
        if (session is null) return;

        var totalScore = await ComputeWeightedTotalScoreAsync(
            session.Id, session.CampaignId, session.CandidateId, session.JobCategory, ct);

        var evt = new SessionScoredEvent
        {
            SessionId = session.Id,
            CampaignId = session.CampaignId,
            CandidateId = session.CandidateId,
            TotalScore = totalScore,
            ScoredAt = DateTime.UtcNow
        };

        _db.OutboxMessages.Add(OutboxMessage.ForScored(evt));

        _logger.LogInformation(
            "Ghi outbox SessionScored: session={SessionId} campaign={CampaignId} score={Score}",
            session.Id, session.CampaignId, totalScore);
    }

    // PAY-13 / BK12 — session đóng mà KHÔNG có answer nào Scored (mọi answer Failed/Skipped) hoặc sinh câu
    // hỏi lỗi (generation_failed) → build SessionAbandonedEvent + ghi outbox-row (Payment RELEASE, không
    // consume): candidate không được chấm 1 answer nào thì không trừ 1 credit (PAY-1). KHÔNG save.
    public async Task EnqueueSessionAbandonedAsync(Guid sessionId, string reason, CancellationToken ct = default)
    {
        var session = await _db.PracticeSessions
            .AsNoTracking()
            .FirstOrDefaultAsync(s => s.Id == sessionId, ct);
        if (session is null) return;

        var evt = new SessionAbandonedEvent
        {
            SessionId = session.Id,
            CampaignId = session.CampaignId,
            CandidateId = session.CandidateId,
            Reason = reason,
            AbandonedAt = DateTime.UtcNow
        };

        _db.OutboxMessages.Add(OutboxMessage.ForAbandoned(evt));

        _logger.LogInformation(
            "Ghi outbox SessionAbandoned: session={SessionId} reason={Reason}", session.Id, reason);
    }

    // Side-effect best-effort SAU khi session đã đóng Scored (đã commit). Chokepoint chung của cả 2 đường
    // đóng (AnswerService callback + PracticeService.SubmitSession). Lỗi KHÔNG chặn (session đã Scored DB).
    public async Task NotifySessionScoredAsync(Guid sessionId, CancellationToken ct = default)
    {
        // BC9: tính + ghi tổng kết điểm buổi luyện B2C (no-op nếu B2B). ComputeAndStore tự SaveChanges.
        try
        {
            await _resultService.ComputeAndStoreAsync(sessionId, ct);
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "BC9: tính tổng kết điểm B2C thất bại cho session {SessionId}", sessionId);
        }

        // BC14/BC15: nếu session này là buổi luyện của 1 roadmap lesson (Practicing) → lesson `Done` +
        // rollup milestone/roadmap. Guard theo session_id + status Practicing → B2B/không-lesson → no-op.
        try
        {
            var doneCount = await _db.RoadmapLessons
                .Where(l => l.SessionId == sessionId && l.Status == LessonStatus.Practicing)
                .ExecuteUpdateAsync(u => u.SetProperty(l => l.Status, LessonStatus.Done), ct);
            if (doneCount > 0)
            {
                _logger.LogInformation("BC14: lesson của session {SessionId} -> Done", sessionId);
                await _roadmapReport.OnLessonDoneAsync(sessionId, ct);
            }
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "BC14/BC15: xử lý lesson Done thất bại cho session {SessionId}", sessionId);
        }

        // BC10: nhận xét chung buổi luyện B2C (AI best-effort). CHỈ B2C (campaign_id null); B2B → no-op.
        // Đọc lại session (đã có overall_score do BC9 vừa ghi) → gọi AIService /summarize-session.
        var session = await _db.PracticeSessions
            .AsNoTracking()
            .FirstOrDefaultAsync(s => s.Id == sessionId, ct);
        if (session is null) return;

        if (session.CampaignId is null)
            await TrySummarizeSessionAsync(session.Id, session.JobCategory, session.Language, session.OverallScore, ct);
    }

    // BC10 — sinh + lưu nhận xét chung buổi B2C. Đọc số liệu BC9 (overall_score + session_criterion_scores)
    // → gọi AIService (sync) → ExecuteUpdate overall_comment. Best-effort: lỗi AI KHÔNG chặn Scored.
    private async Task TrySummarizeSessionAsync(
        Guid sessionId, JobCategory jobCategory, string language, decimal? overallScore, CancellationToken ct)
    {
        try
        {
            var criteria = await _db.SessionCriterionScores
                .AsNoTracking()
                .Where(x => x.SessionId == sessionId)
                .OrderBy(x => x.CreatedAt)
                .Select(x => new SessionSummaryCriterion(x.CriterionName, x.Percentage, x.NeedsImprovement))
                .ToListAsync(ct);

            // Không có breakdown (rubric rỗng / BC9 chưa ghi) → không đủ dữ liệu để nhận xét → bỏ qua.
            if (overallScore is not decimal overall || criteria.Count == 0) return;

            var comment = await _summarizer.SummarizeAsync(jobCategory.ToString(), overall, criteria, language, ct);
            if (string.IsNullOrWhiteSpace(comment)) return;

            await _db.PracticeSessions
                .Where(s => s.Id == sessionId)
                // DB14 — ExecuteUpdate bỏ qua SaveChanges override → stamp updated_at cùng overall_comment.
                .ExecuteUpdateAsync(u => u
                    .SetProperty(s => s.OverallComment, comment)
                    .SetProperty(s => s.UpdatedAt, _ => DateTime.UtcNow), ct);

            _logger.LogInformation("BC10: đã lưu overall_comment cho session {SessionId}", sessionId);
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "BC10: sinh/lưu overall_comment thất bại cho session {SessionId}", sessionId);
        }
    }

    // Điểm tổng có trọng số dùng cho event (ranking B2B — campaign.md §campaign_rankings:
    // "total_score = Σ pct×weight, chuẩn hoá chia Σweight — Interview tính"). Áp dụng chung cho cả B2C.
    private async Task<decimal> ComputeWeightedTotalScoreAsync(
        Guid sessionId, Guid? campaignId, Guid candidateId, JobCategory jobCategory, CancellationToken ct)
    {
        // Nguồn tiêu chí tùy mode (E1, giống AnswerService.TryPublishScoringJobAsync):
        // B2B theo campaign_id, B2C theo job_category + campaign_id IS NULL.
        var criteriaQuery = _db.RubricCriteria.AsNoTracking().Where(c => c.IsActive);
        if (campaignId is Guid cid)
        {
            criteriaQuery = criteriaQuery.Where(c => c.CampaignId == cid);
        }
        else
        {
            // DB30 — đây từng là call-site DUY NHẤT của nhánh B2C KHÔNG đi qua B2CRubricScope: thiếu
            // predicate candidate_id ⇒ materialize rubric RIÊNG của MỌI candidate cùng nghề mỗi lần đóng
            // session. Điểm vẫn đúng ở đường thường (criterion lạ không có score → TryGetValue bỏ qua)
            // nên nó không bao giờ tự lộ ra như bug — chỉ âm thầm quét bảng. Không đúng ở đường rubric
            // đổi giữa chừng: score cũ thuộc scope khác lọt vào weightSum. Đưa về CHUNG resolver (6/6 site).
            var owner = await B2CRubricScope.ResolveOwnerAsync(_db, candidateId, jobCategory, ct);
            criteriaQuery = owner is Guid oid
                ? criteriaQuery.Where(c => c.CampaignId == null && c.CandidateId == oid && c.JobCategory == jobCategory)
                : criteriaQuery.Where(c => c.CampaignId == null && c.CandidateId == null && c.JobCategory == jobCategory);
        }
        var criteria = await criteriaQuery.ToListAsync(ct);
        if (criteria.Count == 0) return 0m;

        var scores = await _db.AnswerScores
            .AsNoTracking()
            .Where(sc => sc.Answer.SessionId == sessionId)
            .Select(sc => new { sc.AnswerId, sc.CriterionId, sc.Score })
            .ToListAsync(ct);
        if (scores.Count == 0) return 0m;

        // E10 — điểm chốt mỗi (answer, criterion) = MEDIAN qua các attempt (self-consistency).
        var medianPerAnswerCriterion = scores
            .GroupBy(s => (s.AnswerId, s.CriterionId))
            .Select(g => new { g.Key.CriterionId, Score = ScoreStatistics.Median(g.Select(s => s.Score)) });

        // Điểm TB mỗi tiêu chí qua các answer đã chấm (BC9 §Công thức bước 1, tái dùng cho B2B).
        var avgByCriterion = medianPerAnswerCriterion
            .GroupBy(s => s.CriterionId)
            .ToDictionary(g => g.Key, g => g.Average(s => s.Score));

        // maxScore khác nhau giữa các tiêu chí ⇒ chuẩn theo % trước khi gộp trọng số.
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
