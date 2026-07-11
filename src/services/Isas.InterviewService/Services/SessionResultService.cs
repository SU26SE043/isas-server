using Isas.InterviewService.ApplicationDbContext;
using Isas.InterviewService.Entities;
using Isas.InterviewService.Models;
using Isas.InterviewService.Services.Interfaces;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Options;

namespace Isas.InterviewService.Services;

// BC9 — tổng kết điểm buổi luyện B2C. Gọi MỘT LẦN khi session chuyển sang Scored (qua
// SessionScoringNotifier — hook chung của cả 2 điểm đóng session: AnswerService.TryCompleteSessionAsync
// + PracticeService.SubmitSessionAsync). GHI DB: practice_sessions.overall_score/answered_count +
// breakdown session_criterion_scores. GET đọc thẳng DB, KHÔNG tính lại. KHÔNG AI (BC10 tách riêng).
public class SessionResultService : ISessionResultService
{
    private readonly InterviewDbContext _db;
    private readonly decimal _improvementThresholdPct;
    private readonly ILogger<SessionResultService> _logger;

    public SessionResultService(
        InterviewDbContext db,
        IOptions<ScoringOptions> options,
        ILogger<SessionResultService> logger)
    {
        _db = db;
        _improvementThresholdPct = options.Value.ImprovementThresholdPct;
        _logger = logger;
    }

    public async Task ComputeAndStoreAsync(Guid sessionId, CancellationToken ct = default)
    {
        var session = await _db.PracticeSessions
            .FirstOrDefaultAsync(s => s.Id == sessionId, ct);
        if (session is null) return;

        // CHỈ B2C (campaign_id null). B2B: điểm tổng phục vụ ranking tính ở CampaignService — không áp BC9.
        if (session.CampaignId is not null) return;

        // Bộ tiêu chí đã chấm (đúng nguồn theo E1): B2C = rubric nghề active + campaign_id IS NULL.
        var criteria = await _db.RubricCriteria.AsNoTracking()
            .Where(c => c.IsActive && c.CampaignId == null && c.JobCategory == session.JobCategory)
            .ToListAsync(ct);

        // Điểm mỗi (answer, criterion) — MATERIALIZE rồi tính trung bình trong C#. KHÔNG dùng AVG SQL:
        // trên SQLite (test) Average(decimal) map hàm ef_avg dễ lệch Postgres → tính LINQ-to-objects.
        var rawScores = await _db.AnswerScores.AsNoTracking()
            .Where(sc => sc.Answer.SessionId == sessionId)
            .Select(sc => new { sc.AnswerId, sc.CriterionId, sc.AttemptNo, sc.Score })
            .ToListAsync(ct);

        // Mỗi (answer, criterion) lấy attempt mới nhất (giống cách hiển thị; self-consistency chưa build).
        var latest = rawScores
            .GroupBy(s => (s.AnswerId, s.CriterionId))
            .Select(g => g.OrderByDescending(s => s.AttemptNo).First())
            .ToList();

        var avgByCriterion = latest
            .GroupBy(s => s.CriterionId)
            .ToDictionary(g => g.Key, g => g.Average(s => s.Score));

        // Câu Skipped/Failed/chưa trả lời không có answer_scores → không tính vào answeredCount.
        var answeredCount = latest.Select(s => s.AnswerId).Distinct().Count();

        // Idempotent: xoá breakdown cũ của session rồi ghi lại (đóng lại cùng session không nhân đôi).
        var existing = await _db.SessionCriterionScores
            .Where(x => x.SessionId == sessionId)
            .ToListAsync(ct);
        if (existing.Count > 0)
            _db.SessionCriterionScores.RemoveRange(existing);

        decimal sumPct = 0m;
        int scoredCriteriaCount = 0;   // K = số tiêu chí đã chấm (có điểm)
        foreach (var c in criteria)
        {
            var hasScore = avgByCriterion.TryGetValue(c.Id, out var avgRaw);
            var maxScore = c.MaxScore > 0 ? c.MaxScore : 1;   // phòng chia 0 (ràng buộc maxScore≥1)
            // Điểm đã kẹp [0,maxScore] ở callback chấm (E8) → percentage nằm [0,100].
            var pct = hasScore
                ? Math.Round(Math.Clamp(avgRaw / maxScore * 100m, 0m, 100m), 2)
                : 0m;
            var average = hasScore ? Math.Round(avgRaw, 2) : 0m;

            if (hasScore)
            {
                sumPct += pct;
                scoredCriteriaCount++;
            }

            _db.SessionCriterionScores.Add(new SessionCriterionScore
            {
                Id = Guid.NewGuid(),
                SessionId = sessionId,
                CriterionId = c.Id,
                CriterionName = c.Name,
                AverageScore = average,
                MaxScore = c.MaxScore,
                Percentage = pct,
                Weight = c.Weight,
                // answeredCount=0 → mọi tiêu chí pct=0 < ngưỡng → needsImprovement = tất cả.
                NeedsImprovement = pct < _improvementThresholdPct,
                CreatedAt = DateTime.UtcNow
            });
        }

        // B2C = TRUNG BÌNH CỘNG pct các tiêu chí (equal weight — INT-10, KHÔNG dùng weight). K=0 → 0.
        var overall = scoredCriteriaCount > 0
            ? Math.Round(Math.Clamp(sumPct / scoredCriteriaCount, 0m, 100m), 2)
            : 0m;

        if (scoredCriteriaCount == 0)
            _logger.LogWarning(
                "BC9: session {SessionId} không có tiêu chí nào được chấm (answered={Answered}) → overall=0",
                sessionId, answeredCount);

        session.OverallScore = overall;
        session.AnsweredCount = answeredCount;

        await _db.SaveChangesAsync(ct);

        _logger.LogInformation(
            "BC9: session {SessionId} overall={Overall} answered={Answered} criteria={K}",
            sessionId, overall, answeredCount, criteria.Count);
    }
}
