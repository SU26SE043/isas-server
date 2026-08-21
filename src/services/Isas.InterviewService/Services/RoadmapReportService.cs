using System.Text.Json;
using Isas.InterviewService.ApplicationDbContext;
using Isas.InterviewService.DTOs;
using Isas.InterviewService.Entities;
using Isas.InterviewService.Enums;
using Isas.InterviewService.Models;
using Isas.InterviewService.Services.Interfaces;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Options;

namespace Isas.InterviewService.Services;

// BC15 (D20) — hoàn tất milestone/roadmap + report. Đọc điểm từ session_criterion_scores (BC9),
// KHÔNG tính lại từ answer_scores; so tiêu chí theo TÊN (rubric đổi version không hồi tố).
//   • Improvement mile N = avg% mile N − avg% mile N−1; mile 1 so baseline (baseline null → hiện điểm đạt).
//   • Radar = avg% per tiêu chí qua MỌI session thuộc roadmap.
//   • levelEvaluation: passed = pct ≥ ngưỡng level (Fresher 50 · Junior 60 · Middle 70 · Senior 80).
//   • Kết luận (strengths/weaknesses/improvements + overallComment): AIService /summarize-roadmap best-effort.
// Final report snapshot vào roadmaps.final_report; interim tính on-read (không lưu).
public class RoadmapReportService : IRoadmapReportService
{
    private readonly InterviewDbContext _db;
    private readonly IAiServiceRoadmapGenerator _generator;
    private readonly RoadmapOptions _options;
    private readonly ILogger<RoadmapReportService> _logger;

    private static readonly JsonSerializerOptions Json = new(JsonSerializerDefaults.Web);

    public RoadmapReportService(
        InterviewDbContext db,
        IAiServiceRoadmapGenerator generator,
        IOptions<RoadmapOptions> options,
        ILogger<RoadmapReportService> logger)
    {
        _db = db;
        _generator = generator;
        _options = options.Value;
        _logger = logger;
    }

    // ── Write path: rollup completion khi 1 lesson vừa Done ─────────────────────────────
    public async Task OnLessonDoneAsync(Guid sessionId, CancellationToken ct = default)
    {
        // Session này có gắn 1 roadmap lesson không? Không → no-op (buổi luyện thường / B2B).
        var milestoneId = await _db.RoadmapLessons.AsNoTracking()
            .Where(l => l.SessionId == sessionId)
            .Select(l => (Guid?)l.MilestoneId)
            .FirstOrDefaultAsync(ct);
        if (milestoneId is null) return;

        // Milestone: đủ MỌI lesson Done chưa? (còn 1 lesson chưa Done → chưa hoàn tất).
        var lessonStatuses = await _db.RoadmapLessons.AsNoTracking()
            .Where(l => l.MilestoneId == milestoneId)
            .Select(l => l.Status)
            .ToListAsync(ct);
        if (lessonStatuses.Count == 0 || lessonStatuses.Any(s => s != LessonStatus.Done))
            return;

        // Load roadmap + milestones (tính improvement cần baseline + mile trước).
        var roadmap = await _db.Roadmaps.AsNoTracking()
            .Include(r => r.Milestones)
            .FirstOrDefaultAsync(r => r.Milestones.Any(m => m.Id == milestoneId), ct);
        if (roadmap is null) return;
        var milestone = roadmap.Milestones.First(m => m.Id == milestoneId);

        // Improvement (jsonb) + milestone Completed. Guard status != Completed → idempotent (absorbing).
        var improvement = await ComputeImprovementAsync(roadmap, milestone, ct);
        var mUpdated = await _db.RoadmapMilestones
            .Where(m => m.Id == milestoneId && m.Status != MilestoneStatus.Completed)
            .ExecuteUpdateAsync(u => u
                .SetProperty(m => m.Status, MilestoneStatus.Completed)
                .SetProperty(m => m.Improvement, improvement)
                .SetProperty(m => m.CompletedAt, DateTime.UtcNow), ct);
        if (mUpdated > 0)
            _logger.LogInformation(
                "BC15: milestone {MilestoneId} -> Completed (roadmap {RoadmapId})", milestoneId, roadmap.Id);

        // Roadmap: đủ MỌI milestone Completed chưa?
        var milestoneStatuses = await _db.RoadmapMilestones.AsNoTracking()
            .Where(m => m.RoadmapId == roadmap.Id)
            .Select(m => m.Status)
            .ToListAsync(ct);
        if (milestoneStatuses.Any(s => s != MilestoneStatus.Completed))
            return;

        // Pre-check Active để tránh gọi AI thừa nếu đã Completed (race cùng 1 user rất hiếm).
        var status = await _db.Roadmaps.AsNoTracking()
            .Where(r => r.Id == roadmap.Id).Select(r => r.Status).FirstAsync(ct);
        if (status != RoadmapStatus.Active) return;

        // Build final report (radar + levelEvaluation + AI kết luận best-effort) → snapshot.
        var report = await BuildReportAsync(roadmap, withAiConclusion: true, ct);
        var json = JsonSerializer.Serialize(report, Json);

        // Guard Status == Active → chỉ 1 lần đóng roadmap (absorbing). AI comment vào cả cột riêng (pattern BC10).
        var rUpdated = await _db.Roadmaps
            .Where(r => r.Id == roadmap.Id && r.Status == RoadmapStatus.Active)
            .ExecuteUpdateAsync(u => u
                .SetProperty(r => r.Status, RoadmapStatus.Completed)
                .SetProperty(r => r.FinalReport, json)
                .SetProperty(r => r.OverallComment, report.OverallComment)
                .SetProperty(r => r.CompletedAt, DateTime.UtcNow), ct);
        if (rUpdated > 0)
            _logger.LogInformation("BC15: roadmap {RoadmapId} -> Completed + final_report snapshot", roadmap.Id);
    }

    // ── Read path: GET /roadmaps/{id}/report ────────────────────────────────────────────
    public async Task<RoadmapReportResponse?> GetReportAsync(
        Guid candidateId, Guid roadmapId, CancellationToken ct = default)
    {
        var roadmap = await _db.Roadmaps.AsNoTracking()
            .Include(r => r.Milestones)
            .FirstOrDefaultAsync(r => r.Id == roadmapId, ct);
        if (roadmap is null) return null;                                     // 404
        if (roadmap.CandidateId != candidateId)
            throw new UnauthorizedAccessException("Không phải roadmap của bạn");   // 403

        // Completed → đọc snapshot, KHÔNG tính lại (kết luận AI đã chốt lúc đóng).
        if (roadmap.Status == RoadmapStatus.Completed && !string.IsNullOrEmpty(roadmap.FinalReport))
        {
            var snapshot = JsonSerializer.Deserialize<RoadmapReportResponse>(roadmap.FinalReport, Json);
            // Ghi đè status bằng trạng thái HIỆN TẠI: snapshot được chốt NGAY TRƯỚC lệnh cập nhật
            // roadmap sang Completed, nên bản thân nó còn mang "Active". Trả thẳng snapshot ra sẽ
            // khiến client gắn nhãn "báo cáo tạm thời" cho một roadmap đã đóng.
            if (snapshot is not null) return snapshot with { RoadmapStatus = roadmap.Status.ToString() };
            _logger.LogWarning("BC15: final_report roadmap {RoadmapId} hỏng → tính interim", roadmapId);
        }

        // Active (hoặc Completed thiếu snapshot — defensive) → interim, KHÔNG gọi AI.
        return await BuildReportAsync(roadmap, withAiConclusion: false, ct);
    }

    // ── Helpers ─────────────────────────────────────────────────────────────────────────

    // radar (avg% per tiêu chí qua MỌI session thuộc roadmap) + levelEvaluation + (tùy chọn) kết luận AI.
    private async Task<RoadmapReportResponse> BuildReportAsync(
        Roadmap roadmap, bool withAiConclusion, CancellationToken ct)
    {
        var scores = await LoadRoadmapCriterionScoresAsync(roadmap.Id, ct);

        // Gộp theo TÊN tiêu chí (rubric đổi version so theo tên). Percentage = radar; các field khác lấy row mới nhất.
        var radar = scores
            .GroupBy(sc => sc.CriterionName)
            .Select(g =>
            {
                var latest = g.OrderByDescending(sc => sc.CreatedAt).First();
                return new CriterionScoreResponse(
                    latest.CriterionId,
                    g.Key,
                    Math.Round(g.Average(x => x.AverageScore), 2),
                    latest.MaxScore,
                    Math.Round(g.Average(x => x.Percentage), 2),
                    latest.Weight);
            })
            .OrderBy(c => c.Name, StringComparer.Ordinal)
            .ToList();

        var threshold = _options.ThresholdFor(roadmap.Level.ToString());
        var levelEval = radar
            .Select(c => new RoadmapLevelEvaluationResponse(
                c.Name, c.Percentage, threshold, c.Percentage >= threshold))
            .ToList();

        IReadOnlyList<string> strengths = [];
        IReadOnlyList<string> weaknesses = [];
        IReadOnlyList<string> improvements = [];
        string? overallComment = null;

        // Kết luận chi tiết chỉ khi build final (Completed). Best-effort: AI lỗi → để rỗng/null, roadmap vẫn Completed.
        if (withAiConclusion && radar.Count > 0)
        {
            try
            {
                var progress = radar.Select(c => new RoadmapCriteriaProgress(
                    c.Name,
                    roadmap.Baseline is not null && roadmap.Baseline.TryGetValue(c.Name, out var bp) ? bp : null,
                    c.Percentage,
                    threshold,
                    c.Percentage >= threshold)).ToList();

                var ai = roadmap.Language == "vi"
                    ? await _generator.SummarizeRoadmapAsync(roadmap.JobCategory.ToString(), roadmap.Level.ToString(), progress, ct)
                    : await _generator.SummarizeRoadmapAsync(roadmap.JobCategory.ToString(), roadmap.Level.ToString(), progress, ct, roadmap.Language);
                strengths = ai.Strengths;
                weaknesses = ai.Weaknesses;
                improvements = ai.Improvements;
                overallComment = ai.OverallComment;
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "BC15: /summarize-roadmap thất bại roadmap {RoadmapId} → kết luận rỗng", roadmap.Id);
            }
        }

        return new RoadmapReportResponse(
            radar, levelEval, strengths, weaknesses, improvements, overallComment, roadmap.Status.ToString());
    }

    // Improvement mile N = avg% mile N − reference; reference = mile N−1 (nếu có điểm) else baseline;
    // mile 1 = baseline.
    //
    // Tiêu chí THIẾU reference (không có mốc để so) → BỎ QUA tiêu chí đó, KHÔNG gán điểm tuyệt đối
    // vào slot "delta". Field này lên UI dưới nhãn "tiến độ" (progress) — gán một điểm số tuyệt
    // đối vào đó là nói cho người học một điều SAI, và sai theo hướng CÓ LỢI (trông như đã tiến bộ).
    // Sự cố thật trên production: một milestone Completed lưu {"Ngữ pháp & dùng từ": 25.01} — đọc
    // report tưởng đâu tiến bộ +25%, thực ra bài chỉ ĐẠT 25/100 điểm, không phải một delta nào cả.
    //
    // Bỏ THEO TỪNG TIÊU CHÍ, không phải "thiếu baseline thì tắt cả milestone": milestone 2 trở đi
    // vẫn so đúng với milestone trước (hành vi đó đang ĐÚNG — không được động vào). Kết quả rỗng
    // hoàn toàn (không tiêu chí nào có mốc) → null, không phải dictionary rỗng — UI/DTO đã có sẵn
    // nhánh null-check (`RoadmapService.cs` Improvement is null → trả null, không phải mảng rỗng).
    private async Task<Dictionary<string, decimal>?> ComputeImprovementAsync(
        Roadmap roadmap, RoadmapMilestone milestone, CancellationToken ct)
    {
        var current = await AvgPctByCriterionForMilestoneAsync(milestone.Id, ct);

        Dictionary<string, decimal>? reference;
        if (milestone.OrderNo <= 1)
        {
            reference = roadmap.Baseline;
        }
        else
        {
            var prev = roadmap.Milestones.FirstOrDefault(m => m.OrderNo == milestone.OrderNo - 1);
            var prevAvg = prev is not null
                ? await AvgPctByCriterionForMilestoneAsync(prev.Id, ct)
                : new Dictionary<string, decimal>();
            reference = prevAvg.Count > 0 ? prevAvg : roadmap.Baseline;
        }

        var improvement = new Dictionary<string, decimal>();
        foreach (var kv in current)
        {
            if (reference is not null && reference.TryGetValue(kv.Key, out var refPct))
                improvement[kv.Key] = Math.Round(kv.Value - refPct, 2);   // delta so mốc
            // Không có mốc cho tiêu chí này → bỏ qua, KHÔNG thêm entry (xem comment ở trên).
        }
        return improvement.Count > 0 ? improvement : null;
    }

    // avg% per tiêu chí (theo TÊN) qua các session Scored gắn các lesson của 1 milestone.
    private async Task<Dictionary<string, decimal>> AvgPctByCriterionForMilestoneAsync(
        Guid milestoneId, CancellationToken ct)
    {
        var sessionIds = await _db.RoadmapLessons.AsNoTracking()
            .Where(l => l.MilestoneId == milestoneId && l.SessionId != null)
            .Select(l => l.SessionId!.Value)
            .ToListAsync(ct);
        if (sessionIds.Count == 0) return new Dictionary<string, decimal>();

        var scores = await _db.SessionCriterionScores.AsNoTracking()
            .Where(sc => sessionIds.Contains(sc.SessionId))
            .ToListAsync(ct);

        return scores
            .GroupBy(sc => sc.CriterionName)
            .ToDictionary(g => g.Key, g => Math.Round(g.Average(x => x.Percentage), 2));
    }

    // Mọi session_criterion_scores của các session Scored gắn lesson (bất kỳ milestone) của roadmap.
    private async Task<List<SessionCriterionScore>> LoadRoadmapCriterionScoresAsync(
        Guid roadmapId, CancellationToken ct)
    {
        var sessionIds = await _db.RoadmapLessons.AsNoTracking()
            .Where(l => l.Milestone.RoadmapId == roadmapId && l.SessionId != null)
            .Select(l => l.SessionId!.Value)
            .ToListAsync(ct);
        if (sessionIds.Count == 0) return new List<SessionCriterionScore>();

        return await _db.SessionCriterionScores.AsNoTracking()
            .Where(sc => sessionIds.Contains(sc.SessionId))
            .ToListAsync(ct);
    }
}
