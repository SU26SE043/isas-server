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
//   • Radar = avg% per tiêu chí qua TỐI ĐA 3 BUỔI GẦN NHẤT (không phải mọi buổi) + progress từng buổi.
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

    /// <summary>
    /// Số buổi GẦN NHẤT dùng để chốt con số hiển thị của một tiêu chí (radar + levelEvaluation).
    ///
    /// <para><b>Vì sao không phải trung bình mọi buổi:</b> trung bình toàn cục che mất xu hướng, và
    /// che theo hướng CÓ LỢI cho người học. Đo trên dữ liệu thật: radar báo ĐẠT 5/6 tiêu chí trong
    /// khi buổi gần nhất chỉ ĐẠT 3/6. Càng nhiều buổi thì một buổi mới càng không nhấc nổi trung
    /// bình (+20 điểm khi mới có 1 buổi, chỉ +6,7 khi đã có 5) ⇒ "luyện lại để nâng điểm" trở thành
    /// vô nghĩa đúng lúc người học cần nó nhất.</para>
    ///
    /// <para><b>Vì sao 3 chứ không phải 1:</b> điểm mỗi buổi bị lượng tử hoá rất thô — rubric 1..5 sao
    /// nên một tiêu chí chỉ nhận được 20/40/60/80/100. Một buổi đơn lẻ quá nhiễu để phán
    /// "Đạt/Chưa đạt"; 3 buổi vừa đủ làm phẳng nhiễu mà vẫn bám sát hiện tại.</para>
    /// </summary>
    private const int RecentSessionWindow = 3;

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
            if (snapshot is not null)
                return snapshot with
                {
                    RoadmapStatus = roadmap.Status.ToString(),
                    // Snapshot chốt TRƯỚC khi có `progress` không mang khoá đó ⇒ deserialize gán null
                    // vào một thuộc tính khai là non-nullable ⇒ NRE ngay khi serialize trả về (hoặc ở
                    // client, tuỳ chỗ chạm trước). Chuẩn hoá về rỗng: "lộ trình cũ không có dữ liệu
                    // diễn tiến" — đó là sự thật, không phải lỗi.
                    // ⚠ Mọi field THÊM vào RoadmapReportResponse sau này đều cần đúng một dòng như
                    // dòng này, vì snapshot cũ nằm sẵn trong DB và không migration nào chạm tới.
                    Progress = snapshot.Progress ?? []
                };
            _logger.LogWarning("BC15: final_report roadmap {RoadmapId} hỏng → tính interim", roadmapId);
        }

        // Active (hoặc Completed thiếu snapshot — defensive) → interim, KHÔNG gọi AI.
        return await BuildReportAsync(roadmap, withAiConclusion: false, ct);
    }

    // ── Helpers ─────────────────────────────────────────────────────────────────────────

    // radar (avg% per tiêu chí qua TỐI ĐA `RecentSessionWindow` buổi gần nhất) + progress từng buổi
    // + levelEvaluation + (tùy chọn) kết luận AI.
    private async Task<RoadmapReportResponse> BuildReportAsync(
        Roadmap roadmap, bool withAiConclusion, CancellationToken ct)
    {
        // Đã sắp theo thứ tự thời gian được chấm (cũ → mới).
        var sessions = await LoadRoadmapSessionsAsync(roadmap.Id, ct);

        // Gộp theo TÊN tiêu chí (rubric đổi version so theo tên) rồi theo BUỔI: cửa sổ "3 buổi gần
        // nhất" phải đếm theo BUỔI, không theo dòng điểm. `GroupBy` của LINQ-to-Objects giữ thứ tự
        // xuất hiện đầu tiên ⇒ thứ tự thời gian của `sessions` được bảo toàn xuống tận đây.
        var radar = sessions
            .SelectMany(s => s.Scores.Select(sc => (Session: s, Score: sc)))
            .GroupBy(x => x.Score.CriterionName)
            .Select(g =>
            {
                var perSession = g
                    .GroupBy(x => x.Session.SessionId)
                    .Select(sg => new
                    {
                        Pct = sg.Average(x => x.Score.Percentage),
                        Avg = sg.Average(x => x.Score.AverageScore)
                    })
                    .ToList();

                var recent = perSession.TakeLast(RecentSessionWindow).ToList();
                var latest = g.Last().Score;   // buổi gần nhất → id/maxScore/weight (snapshot rubric mới nhất)

                return new RoadmapRadarCriterionResponse(
                    latest.CriterionId,
                    g.Key,
                    latest.MaxScore,
                    latest.Weight,
                    Math.Round(recent.Average(x => x.Pct), 2),
                    Math.Round(recent.Average(x => x.Avg), 2),
                    // Chỉ có 1 buổi ⇒ không có mốc xuất phát để so. Trả null (= KHÔNG BIẾT) thay vì
                    // lặp lại chính điểm hiện tại, vốn sẽ hiện thành "tiến bộ 0%" — một kết luận sai.
                    perSession.Count > 1 ? Math.Round(perSession[0].Pct, 2) : null,
                    perSession.Count,
                    recent.Count);
            })
            .OrderBy(c => c.Name, StringComparer.Ordinal)
            .ToList();

        var sessionProgress = sessions
            .Select((s, i) =>
            {
                var perCriterion = s.Scores
                    .GroupBy(sc => sc.CriterionName)
                    .Select(cg => new RoadmapProgressCriterionResponse(
                        cg.Key, Math.Round(cg.Average(x => x.Percentage), 2)))
                    .OrderBy(c => c.Name, StringComparer.Ordinal)
                    .ToList();

                return new RoadmapSessionProgressResponse(
                    i + 1,
                    s.LessonTitle,
                    s.ScoredAt,
                    // Trung bình cộng các tiêu chí CỦA CHÍNH buổi đó (equal weight, như INT-10 B2C).
                    Math.Round(perCriterion.Average(c => c.Percentage), 2),
                    perCriterion);
            })
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
                // Mốc so cho AI, theo THỨ TỰ ƯU TIÊN — đừng đảo:
                //   ① baseline đo LÚC TẠO lộ trình (trước khi học) — mốc tốt nhất;
                //   ② % ở buổi ĐẦU TIÊN trong lộ trình (đã học rồi, nên chỉ là dự phòng);
                //   ③ null = KHÔNG BIẾT.
                //
                // Vì sao cần ②: prompt /summarize-roadmap định nghĩa "cải thiện" là so với baseline,
                // mà roadmap tạo khi người dùng chưa luyện buổi nào thì `Baseline` là null — đo được
                // 86% roadmap rơi vào ca đó ⇒ model không có gì để so nên liệt kê SẠCH tiêu chí vào
                // mục "cần cải thiện", kể cả những tiêu chí nó vừa xếp vào "điểm mạnh".
                //
                // ⚠ ③ phải giữ đúng null: prompt đã có nhánh in "chưa có baseline". Lấp bằng
                // `c.Percentage` làm mọi tiêu chí trông như KHÔNG tiến bộ; lấp bằng 0 làm mọi tiêu chí
                // trông như tiến bộ vượt bậc — cả hai đều bịa, và cái sau bịa theo hướng khen.
                var progress = radar.Select(c => new RoadmapCriteriaProgress(
                    c.Name,
                    roadmap.Baseline is not null && roadmap.Baseline.TryGetValue(c.Name, out var bp)
                        ? bp
                        : c.StartPercentage,
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
            radar, levelEval, strengths, weaknesses, improvements, overallComment,
            roadmap.Status.ToString(), sessionProgress);
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

    // Điểm của 1 buổi luyện thuộc roadmap, kèm mốc thời gian + tên bài học để dựng đường xu hướng.
    private sealed record RoadmapSessionScores(
        Guid SessionId, string LessonTitle, DateTime ScoredAt, List<SessionCriterionScore> Scores);

    // Các buổi (đã có breakdown điểm) gắn lesson của roadmap, XẾP THEO THỜI GIAN CHẤM (cũ → mới).
    //
    // Mốc thời gian = max(session_criterion_scores.created_at) của buổi — tức lúc buổi được CHẤM
    // (SessionResultService ghi bảng này khi session -> Scored). KHÔNG dùng practice_sessions
    // .completed_at: mốc đó là lúc BẤM NỘP, và nó nullable — trong khi mốc ở đây chắc chắn tồn tại
    // cho đúng tập dòng ta phát ra (chỉ buổi CÓ breakdown mới lọt vào danh sách này).
    //
    // Một thứ tự DUY NHẤT dùng cho cả cửa sổ "3 buổi gần nhất" của radar lẫn thứ tự của progress —
    // hai bên xài hai khoá khác nhau thì "3 buổi gần nhất" trên radar sẽ không phải 3 điểm cuối của
    // đường xu hướng, và người đọc không có cách nào biết.
    private async Task<List<RoadmapSessionScores>> LoadRoadmapSessionsAsync(
        Guid roadmapId, CancellationToken ct)
    {
        var lessons = await _db.RoadmapLessons.AsNoTracking()
            .Where(l => l.Milestone.RoadmapId == roadmapId && l.SessionId != null)
            .OrderBy(l => l.Milestone.OrderNo).ThenBy(l => l.OrderNo)
            .Select(l => new { SessionId = l.SessionId!.Value, l.Title })
            .ToListAsync(ct);
        if (lessons.Count == 0) return [];

        var sessionIds = lessons.Select(x => x.SessionId).Distinct().ToList();
        var scores = await _db.SessionCriterionScores.AsNoTracking()
            .Where(sc => sessionIds.Contains(sc.SessionId))
            .ToListAsync(ct);
        if (scores.Count == 0) return [];

        // 1 buổi ↔ 1 lesson (RoadmapLesson.SessionId set lúc /start). Không ràng buộc nào chặn hai
        // lesson trỏ chung 1 session, nên chốt deterministic: lesson đầu theo (milestone, lesson).
        var titleBySession = lessons
            .GroupBy(x => x.SessionId)
            .ToDictionary(g => g.Key, g => g.First().Title);

        return scores
            .GroupBy(sc => sc.SessionId)
            .Select(g => new RoadmapSessionScores(
                g.Key,
                titleBySession.TryGetValue(g.Key, out var title) ? title : string.Empty,
                g.Max(sc => sc.CreatedAt),
                g.ToList()))
            // Tiebreak bằng SessionId: hai buổi chấm xong trong cùng một tick sẽ làm "3 buổi gần
            // nhất" thành không xác định nếu chỉ so mốc thời gian.
            .OrderBy(s => s.ScoredAt).ThenBy(s => s.SessionId)
            .ToList();
    }
}
