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
    private readonly ISessionResultService _resultService;
    private readonly IAiServiceSessionSummarizer _summarizer;   // BC10
    private readonly IRoadmapReportService _roadmapReport;      // BC15
    private readonly ILogger<SessionScoringNotifier> _logger;

    public SessionScoringNotifier(
        InterviewDbContext db,
        ISessionEventPublisher eventPublisher,
        ISessionResultService resultService,
        IAiServiceSessionSummarizer summarizer,
        IRoadmapReportService roadmapReport,
        ILogger<SessionScoringNotifier> logger)
    {
        _db = db;
        _eventPublisher = eventPublisher;
        _resultService = resultService;
        _summarizer = summarizer;
        _roadmapReport = roadmapReport;
        _logger = logger;
    }

    public async Task NotifySessionScoredAsync(Guid sessionId, CancellationToken ct = default)
    {
        // BC9: tính + ghi tổng kết điểm buổi luyện B2C (no-op nếu B2B). Đây là hook chung của cả 2
        // điểm đóng session (AnswerService.TryCompleteSessionAsync + PracticeService.SubmitSessionAsync).
        // Best-effort: lỗi tính KHÔNG chặn việc đóng session (đã Scored trong DB) — overall_score để
        // null, có thể backfill sau. ComputeAndStore tự SaveChanges nên context không rớt dở dang.
        try
        {
            await _resultService.ComputeAndStoreAsync(sessionId, ct);
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "BC9: tính tổng kết điểm B2C thất bại cho session {SessionId}", sessionId);
        }

        // BC14: nếu session này là buổi luyện của 1 roadmap lesson (Practicing) → lesson `Done`.
        // Đóng session Scored là chokepoint chung của cả 2 đường (AnswerService callback +
        // PracticeService.SubmitSession) nên đặt ở đây phủ hết. Guard theo session_id + status
        // Practicing → chỉ chạm lesson gắn đúng session này; B2B/không-lesson → no-op. Best-effort
        // (lỗi KHÔNG chặn đóng session — session đã Scored trong DB).
        try
        {
            var doneCount = await _db.RoadmapLessons
                .Where(l => l.SessionId == sessionId && l.Status == LessonStatus.Practicing)
                .ExecuteUpdateAsync(u => u.SetProperty(l => l.Status, LessonStatus.Done), ct);
            if (doneCount > 0)
            {
                _logger.LogInformation("BC14: lesson của session {SessionId} -> Done", sessionId);

                // BC15: lesson vừa Done → rollup hoàn tất milestone (+improvement) / roadmap (+final_report,
                // AI comment). Best-effort trong cùng try — lỗi KHÔNG chặn đóng session (đã Scored trong DB).
                await _roadmapReport.OnLessonDoneAsync(sessionId, ct);
            }
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "BC14/BC15: xử lý lesson Done thất bại cho session {SessionId}", sessionId);
        }

        var session = await _db.PracticeSessions
            .AsNoTracking()
            .FirstOrDefaultAsync(s => s.Id == sessionId, ct);
        if (session is null) return;

        // BC10: nhận xét chung buổi luyện B2C (AI best-effort). CHỈ B2C (campaign_id null); B2B → no-op.
        // Chạy SAU BC9 (đã lưu overall_score + session_criterion_scores) → đọc lại số liệu đó → gọi
        // AIService /summarize-session → lưu practice_sessions.overall_comment (AI KHÔNG ghi DB — GEN-4).
        // Best-effort: AI lỗi/timeout → overall_comment để null, KHÔNG chặn Scored (session đã Scored trong DB).
        if (session.CampaignId is null)
            await TrySummarizeSessionAsync(session.Id, session.JobCategory, session.OverallScore, ct);

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

    // BC10 — sinh + lưu nhận xét chung buổi B2C. Đọc số liệu BC9 (overall_score + session_criterion_scores)
    // đã lưu → gọi AIService (sync) → ExecuteUpdate overall_comment. Best-effort: bọc try/catch, lỗi AI KHÔNG
    // chặn Scored (giống BC9/BC14). AI trả rỗng → AiServiceException → nuốt, overall_comment giữ null (backfill sau).
    private async Task TrySummarizeSessionAsync(
        Guid sessionId, JobCategory jobCategory, decimal? overallScore, CancellationToken ct)
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

            var comment = await _summarizer.SummarizeAsync(jobCategory.ToString(), overall, criteria, ct);
            if (string.IsNullOrWhiteSpace(comment)) return;

            await _db.PracticeSessions
                .Where(s => s.Id == sessionId)
                .ExecuteUpdateAsync(u => u.SetProperty(s => s.OverallComment, comment), ct);

            _logger.LogInformation("BC10: đã lưu overall_comment cho session {SessionId}", sessionId);
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "BC10: sinh/lưu overall_comment thất bại cho session {SessionId}", sessionId);
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
