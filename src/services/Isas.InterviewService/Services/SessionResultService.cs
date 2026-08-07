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

        // Bộ tiêu chí đã chấm (đúng nguồn theo E1/BC16): B2C = rubric nghề active + campaign_id IS NULL,
        // ưu tiên rubric RIÊNG của candidate (nếu có active), else seed mặc định (candidate_id IS NULL).
        var owner = await B2CRubricScope.ResolveOwnerAsync(_db, session.CandidateId, session.JobCategory, session.Language, ct);
        // Q8 — `c.Language` BẮT BUỘC: seed B2C chứa CẢ vi lẫn en cùng nghề, nên thiếu vế này thì mọi buổi
        // (kể cả buổi tiếng Việt) nạp 14 tiêu chí thay vì 7. Bảy tiêu chí ngôn ngữ kia không bao giờ có
        // answer_scores (đường chấm đã lọc đúng) ⇒ ghi xuống thành 7 dòng `0.00` gắn cờ needs_improvement.
        // `overallScore` không sai (scoredCriteriaCount loại chúng) nhưng session_criterion_scores là
        // nguồn của BC12 weakness / BC15 đo cải thiện / F14 peer benchmark ⇒ dữ liệu bẩn chảy tiếp.
        var critQuery = _db.RubricCriteria.AsNoTracking()
            .Where(c => c.IsActive && c.CampaignId == null && c.JobCategory == session.JobCategory
                        && c.Language == session.Language);
        critQuery = owner is Guid oid
            ? critQuery.Where(c => c.CandidateId == oid)
            : critQuery.Where(c => c.CandidateId == null);
        var criteria = await critQuery.ToListAsync(ct);

        // Điểm mỗi (answer, criterion) — MATERIALIZE rồi tính trong C#. KHÔNG dùng AVG SQL:
        // trên SQLite (test) Average(decimal) map hàm ef_avg dễ lệch Postgres → tính LINQ-to-objects.
        var rawScores = await _db.AnswerScores.AsNoTracking()
            .Where(sc => sc.Answer.SessionId == sessionId)
            .Select(sc => new { sc.AnswerId, sc.CriterionId, sc.Score })
            .ToListAsync(ct);

        // E10 — điểm chốt mỗi (answer, criterion) = MEDIAN qua các attempt (self-consistency),
        // thay cho "attempt mới nhất". N=1 → median-of-1 = giá trị cũ (không đổi hành vi).
        var median = rawScores
            .GroupBy(s => (s.AnswerId, s.CriterionId))
            .Select(g => new { g.Key.AnswerId, g.Key.CriterionId, Score = ScoreStatistics.Median(g.Select(s => s.Score)) })
            .ToList();

        var avgByCriterion = median
            .GroupBy(s => s.CriterionId)
            .ToDictionary(g => g.Key, g => g.Average(s => s.Score));

        // Câu Skipped/Failed/chưa trả lời không có answer_scores → không tính vào answeredCount.
        var answeredCount = median.Select(s => s.AnswerId).Distinct().Count();

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
            // Tiêu chí KHÔNG có answer_scores nào trong buổi ⇒ KHÔNG ghi dòng.
            //
            // Trước đây ghi dòng `0.00` + `needs_improvement = true`, nghĩa là "chưa từng được hỏi"
            // bị ghi xuống y như "trả lời rất tệ". Bảng này là NGUỒN của BC12 (điểm yếu → sinh lộ
            // trình ôn), BC15 (đo cải thiện) và F14 (mốc so sánh với người khác) ⇒ dữ liệu bẩn không
            // dừng ở màn hình kết quả mà chảy tiếp vào lộ trình học và vào mốc của cả cộng đồng.
            // Kể từ khi phạm vi chấm thu hẹp theo câu hỏi, ca này thành ca THƯỜNG GẶP chứ không còn
            // là ngoại lệ (buổi 5 câu không thể chạm hết mọi tiêu chí nội dung).
            //
            // KHÔNG ghi (thay vì ghi kèm cờ "chưa đánh giá") vì cả 5 nơi đọc bảng này đều chỉ duyệt
            // các dòng CÓ SẴN và gom theo tên tiêu chí — không nơi nào cần "đủ mọi tiêu chí":
            //   BC10 SessionScoringNotifier · BC12 RoadmapService · BC15 RoadmapReportService ·
            //   F14 CriterionBenchmarkService · GET kết quả PracticeService/CvVsAnswerReportBuilder.
            // Thêm cờ thì phải dạy cả 5 nơi bỏ qua nó, và nơi nào quên là quay lại đúng bug này.
            if (!avgByCriterion.TryGetValue(c.Id, out var avgRaw)) continue;

            var maxScore = c.MaxScore > 0 ? c.MaxScore : 1;   // phòng chia 0 (ràng buộc maxScore≥1)
            // Điểm đã kẹp [0,maxScore] ở callback chấm (E8) → percentage nằm [0,100].
            var pct = Math.Round(Math.Clamp(avgRaw / maxScore * 100m, 0m, 100m), 2);

            sumPct += pct;
            scoredCriteriaCount++;

            _db.SessionCriterionScores.Add(new SessionCriterionScore
            {
                Id = Guid.NewGuid(),
                SessionId = sessionId,
                CriterionId = c.Id,
                CriterionName = c.Name,
                AverageScore = Math.Round(avgRaw, 2),
                MaxScore = c.MaxScore,
                Percentage = pct,
                Weight = c.Weight,
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
