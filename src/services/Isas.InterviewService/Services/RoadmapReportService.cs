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
//   • levelEvaluation: passed = pct ≥ ngưỡng level — ngưỡng lấy từ roadmap_level_thresholds (admin
//     chỉnh runtime), chưa ai chỉnh thì rơi về mặc định RoadmapOptions (Fresher 50 · Junior 60 ·
//     Middle 70 · Senior 80). Ngưỡng CHỐT lúc build → vào snapshot, KHÔNG hồi tố report đã đóng.
//   • Kết luận (strengths/weaknesses/improvements + overallComment): AIService /summarize-roadmap best-effort.
// Final report snapshot vào roadmaps.final_report; interim tính on-read (không lưu).
public class RoadmapReportService : IRoadmapReportService
{
    private readonly InterviewDbContext _db;
    private readonly IAiServiceRoadmapGenerator _generator;
    private readonly IRoadmapThresholdService _thresholds;
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
        IRoadmapThresholdService thresholds,
        ILogger<RoadmapReportService> logger)
    {
        _db = db;
        _generator = generator;
        _thresholds = thresholds;
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

        // Improvement (jsonb) + PHẦN TÍNH ra nó (score_snapshot) + milestone Completed.
        // Guard status != Completed → idempotent (absorbing).
        //
        // ⚠ Hai cột phải đi trong CÙNG một ExecuteUpdate và sinh ra từ CÙNG một vòng lặp
        // (`ComputeMilestoneScoreAsync`). Tách ra thì "con số ở tiêu đề" và "phần tính cộng ra nó"
        // lệch được — đúng thứ score_snapshot sinh ra để chống.
        var (improvement, snapshot) = await ComputeMilestoneScoreAsync(roadmap, milestone, ct);
        var mUpdated = await _db.RoadmapMilestones
            .Where(m => m.Id == milestoneId && m.Status != MilestoneStatus.Completed)
            .ExecuteUpdateAsync(u => u
                .SetProperty(m => m.Status, MilestoneStatus.Completed)
                .SetProperty(m => m.Improvement, improvement)
                .SetProperty(m => m.ScoreSnapshot, snapshot)
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

    // ── Read path: GET /roadmaps/{id}/milestones/{mid}/score-report ─────────────────────
    //
    // "Phần tính" đứng sau con số delta hiển thị ở trang lộ trình. Mọi chặng đều xem được — điểm
    // từng tiêu chí + các buổi đã cộng vào là thông tin có ích ngay cả khi chưa có mốc để so.
    public async Task<MilestoneScoreReportResponse?> GetMilestoneScoreReportAsync(
        Guid candidateId, Guid roadmapId, Guid milestoneId, CancellationToken ct = default)
    {
        var roadmap = await _db.Roadmaps.AsNoTracking()
            .Include(r => r.Milestones)
            .FirstOrDefaultAsync(r => r.Id == roadmapId, ct);
        if (roadmap is null) return null;                                          // 404
        if (roadmap.CandidateId != candidateId)
            throw new UnauthorizedAccessException("Không phải roadmap của bạn");    // 403

        // Chặng không thuộc lộ trình này → 404 (chứ không phải 403): với người KHÔNG sở hữu lộ
        // trình ta đã 403 ở trên rồi, nên tới được đây nghĩa là chủ sở hữu hỏi một id không có
        // trong lộ trình của chính mình.
        var milestone = roadmap.Milestones.FirstOrDefault(m => m.Id == milestoneId);
        if (milestone is null) return null;                                        // 404

        // Đã chốt sổ → ĐỌC, không tính lại. Đây là điều duy nhất bảo đảm phần tính cộng ra đúng con
        // số ở tiêu đề kể cả sau khi người học luyện lại một bài của chặng.
        if (milestone.ScoreSnapshot is not null)
            return MapMilestoneScore(milestone, milestone.ScoreSnapshot, MilestoneScoreSource.Snapshot);

        var (_, snapshot) = await ComputeMilestoneScoreAsync(roadmap, milestone, ct);

        // Chưa hoàn thành → chưa có delta nào được chốt, tính lúc đọc là đúng và không lệch được gì.
        // Đã hoàn thành mà không có snapshot → chặng chốt sổ TRƯỚC bản này: ta CỐ Ý không backfill
        // (xem MapMilestoneScore) và gắn nhãn `recomputed` để client nói rõ đây là số tính lại.
        var source = milestone.Status == MilestoneStatus.Completed
            ? MilestoneScoreSource.Recomputed
            : MilestoneScoreSource.Computed;
        return MapMilestoneScore(milestone, snapshot, source);
    }

    /// <summary>
    /// Snapshot (đã lưu hoặc vừa tính) → response, gắn kèm <c>headlineDeltaPct</c> = chính con số
    /// đang hiện ở tiêu đề, đọc thẳng từ <c>improvement</c>.
    ///
    /// <para><b>Vì sao trả cả hai con số:</b> với <c>snapshot</c> chúng luôn bằng nhau (cùng một
    /// vòng lặp ghi ra) nên đây là phép tự kiểm; với <c>recomputed</c> chúng có thể khác, và lúc đó
    /// sai lệch phải NHÌN THẤY ĐƯỢC chứ không âm thầm — client có đủ dữ liệu để cảnh báo.</para>
    ///
    /// <para><b>Vì sao KHÔNG backfill chặng cũ:</b> backfill = tính lại từ dữ liệu HIỆN TẠI rồi
    /// đóng dấu "đã chốt". Với chặng có bài đã luyện lại, con số đó mâu thuẫn với <c>improvement</c>
    /// mà UI đang hiện, và mâu thuẫn đó sẽ KHÔNG còn dấu vết nào để nhận ra — đúng thất bại mà cột
    /// snapshot sinh ra để chống. Giữ null = KHÔNG BIẾT (BK23) và nói ra bằng <c>source</c>.</para>
    /// </summary>
    private static MilestoneScoreReportResponse MapMilestoneScore(
        RoadmapMilestone milestone, MilestoneScoreSnapshot snapshot, string source)
    {
        var headline = milestone.Improvement;

        var criteria = snapshot.Criteria
            .Select(c => new MilestoneScoreCriterionResponse(
                c.Name,
                c.CurrentAveragePercentage,
                c.CurrentSessions.Select(MapMilestoneScoreSession).ToList(),
                c.ReferenceAveragePercentage,
                c.ReferenceSessions.Select(MapMilestoneScoreSession).ToList(),
                c.DeltaPct,
                headline is not null && headline.TryGetValue(c.Name, out var h) ? h : null))
            .ToList();

        return new MilestoneScoreReportResponse(
            milestone.Id,
            milestone.Title,
            milestone.OrderNo,
            milestone.Status.ToString(),
            source,
            snapshot.ComparedWith,
            snapshot.ComparedWithTitle,
            criteria);
    }

    private static MilestoneScoreSessionResponse MapMilestoneScoreSession(MilestoneScoreSessionSnapshot s)
        => new(s.SessionId, s.LessonTitle, s.AttemptNo, s.Percentage, s.ScoredAt);

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

        // Ngưỡng đọc từ DB (admin chỉnh runtime), rơi về mặc định code khi chưa ai chỉnh.
        // CHỐT tại đây rồi đi vào snapshot final_report ⇒ lộ trình đã đóng sổ KHÔNG bị ngưỡng mới
        // sửa lại kết luận (xem GetReportAsync: Completed đọc thẳng snapshot).
        var threshold = await _thresholds.ThresholdForAsync(roadmap.Level.ToString(), ct);
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
    // mile 1 = baseline. Trả về CẢ hai thứ được chốt cùng lúc: dictionary delta (lên tiêu đề UI) và
    // phần TÍNH đầy đủ ra nó (score_snapshot) — xem <see cref="MilestoneScoreSnapshot"/>.
    //
    // Tiêu chí THIẾU reference (không có mốc để so) → delta BỎ QUA tiêu chí đó, KHÔNG gán điểm tuyệt
    // đối vào slot "delta". Field này lên UI dưới nhãn "tiến độ" (progress) — gán một điểm số tuyệt
    // đối vào đó là nói cho người học một điều SAI, và sai theo hướng CÓ LỢI (trông như đã tiến bộ).
    // Sự cố thật trên production: một milestone Completed lưu {"Ngữ pháp & dùng từ": 25.01} — đọc
    // report tưởng đâu tiến bộ +25%, thực ra bài chỉ ĐẠT 25/100 điểm, không phải một delta nào cả.
    // (Snapshot vẫn liệt kê tiêu chí đó với deltaPct = null — "chưa có gì để so", khác hẳn 0.)
    //
    // Bỏ THEO TỪNG TIÊU CHÍ, không phải "thiếu baseline thì tắt cả milestone": milestone 2 trở đi
    // vẫn so đúng với milestone trước (hành vi đó đang ĐÚNG — không được động vào). Kết quả rỗng
    // hoàn toàn (không tiêu chí nào có mốc) → null, không phải dictionary rỗng — UI/DTO đã có sẵn
    // nhánh null-check (`RoadmapService.cs` Improvement is null → trả null, không phải mảng rỗng).
    private async Task<(Dictionary<string, decimal>? Improvement, MilestoneScoreSnapshot Snapshot)>
        ComputeMilestoneScoreAsync(Roadmap roadmap, RoadmapMilestone milestone, CancellationToken ct)
    {
        var current = await LoadMilestoneBreakdownAsync(milestone.Id, ct);

        // Chọn mốc — giữ NGUYÊN XI luật cũ (đường tính delta không được đổi hành vi), chỉ rút thêm
        // NHÃN và chi tiết buổi của mốc để hiển thị.
        Dictionary<string, decimal>? reference;
        string? comparedWithTitle = null;
        List<MilestoneScoreCriterionSnapshot>? referenceBreakdown = null;

        if (milestone.OrderNo <= 1)
        {
            reference = roadmap.Baseline;
        }
        else
        {
            var prev = roadmap.Milestones.FirstOrDefault(m => m.OrderNo == milestone.OrderNo - 1);
            var prevBreakdown = prev is not null
                ? await LoadMilestoneBreakdownAsync(prev.Id, ct)
                : [];
            var prevAvg = prevBreakdown.ToDictionary(c => c.Name, c => c.CurrentAveragePercentage);
            if (prevAvg.Count > 0)
            {
                reference = prevAvg;
                comparedWithTitle = prev!.Title;
                referenceBreakdown = prevBreakdown;
            }
            else
            {
                reference = roadmap.Baseline;
            }
        }

        // Nhãn nói đúng mốc THỰC SỰ dùng được. `baseline` rỗng/null đều → "none": gắn nhãn
        // "baseline" cho một mốc không có tiêu chí nào là hứa một phép so không tồn tại.
        var comparedWith = referenceBreakdown is not null
            ? MilestoneScoreReference.PreviousMilestone
            : reference is { Count: > 0 } ? MilestoneScoreReference.Baseline : MilestoneScoreReference.None;

        // Buổi của mốc CHỈ có khi mốc là chặng liền trước. `baseline` là một snapshot số đo lúc lập
        // lộ trình, không có buổi nào đứng sau nó ⇒ rỗng (không phải null — "không có buổi nào", chứ
        // không phải "không biết").
        var referenceSessions = referenceBreakdown?
            .ToDictionary(c => c.Name, c => c.CurrentSessions)
            ?? [];

        // MỘT vòng lặp sinh ra CẢ delta lên tiêu đề LẪN phần tính — đây là chỗ khiến hai bên không
        // thể lệch nhau do cấu trúc.
        var improvement = new Dictionary<string, decimal>();
        var criteria = new List<MilestoneScoreCriterionSnapshot>(current.Count);
        foreach (var c in current)
        {
            decimal? refPct = null;
            decimal? delta = null;
            if (reference is not null && reference.TryGetValue(c.Name, out var rp))
            {
                refPct = rp;
                delta = Math.Round(c.CurrentAveragePercentage - rp, 2);   // delta so mốc
                improvement[c.Name] = delta.Value;
            }
            // Không có mốc cho tiêu chí này → KHÔNG thêm entry vào improvement (xem comment ở trên),
            // nhưng VẪN liệt kê trong snapshot: "chặng này được bao nhiêu, từ những buổi nào" là
            // thông tin có ích ngay cả khi chưa có gì để so.

            criteria.Add(c with
            {
                ReferenceAveragePercentage = refPct,
                ReferenceSessions = referenceSessions.TryGetValue(c.Name, out var rs) ? rs : [],
                DeltaPct = delta
            });
        }

        return (
            improvement.Count > 0 ? improvement : null,
            new MilestoneScoreSnapshot(comparedWith, comparedWithTitle, criteria));
    }

    /// <summary>
    /// Điểm từng tiêu chí của một chặng, KÈM đúng những dòng điểm đã cộng vào nó.
    ///
    /// <para><b>Đây là đường tính DUY NHẤT</b> cho cả delta (<c>improvement</c>) lẫn phần tính hiển
    /// thị. Viết một truy vấn thứ hai để "tính lại cho tiện" thì hai bên trôi xa nhau và triệu chứng
    /// là <i>phần tính không cộng ra con số ở tiêu đề</i> — đúng thứ tính năng này sinh ra để chống.</para>
    ///
    /// <para><b>Trung bình tính trên DÒNG điểm, không trên buổi</b> — giữ nguyên xi phép tính cũ. Hai
    /// thứ chỉ khác nhau khi một buổi có hai dòng cùng TÊN tiêu chí (rubric đổi version, id khác
    /// nhau nhưng tên trùng); lúc đó cả hai dòng đều được liệt kê ⇒ trung bình của danh sách hiển
    /// thị vẫn đúng bằng con số chốt.</para>
    ///
    /// <para>⚠ Nguồn buổi là <c>roadmap_lessons.session_id</c> = buổi MỚI NHẤT của mỗi bài, nên một
    /// bài đã luyện lại chỉ đóng góp lần làm gần nhất. Đó là hành vi sẵn có của phép tính delta;
    /// <c>AttemptNo</c> được trả kèm để người học thấy "Lần 2" thay vì tưởng mất một buổi.</para>
    /// </summary>
    private async Task<List<MilestoneScoreCriterionSnapshot>> LoadMilestoneBreakdownAsync(
        Guid milestoneId, CancellationToken ct)
    {
        var lessons = await _db.RoadmapLessons.AsNoTracking()
            .Where(l => l.MilestoneId == milestoneId && l.SessionId != null)
            .Select(l => new { SessionId = l.SessionId!.Value, l.Title })
            .ToListAsync(ct);
        if (lessons.Count == 0) return [];

        var sessionIds = lessons.Select(x => x.SessionId).Distinct().ToList();

        var scores = await _db.SessionCriterionScores.AsNoTracking()
            .Where(sc => sessionIds.Contains(sc.SessionId))
            .ToListAsync(ct);
        if (scores.Count == 0) return [];

        // Lần làm thứ mấy — chỉ để hiển thị. Buổi không có dòng lần-làm nào (dữ liệu trước khi có
        // bảng đó) → null = KHÔNG BIẾT, không bịa thành 1.
        var attemptNoBySession = await _db.RoadmapLessonAttempts.AsNoTracking()
            .Where(a => sessionIds.Contains(a.SessionId))
            .ToDictionaryAsync(a => a.SessionId, a => a.AttemptNo, ct);

        // Chốt deterministic (dòng đầu tiên) — không ràng buộc nào chặn hai lesson trỏ chung 1 buổi.
        var titleBySession = lessons
            .GroupBy(x => x.SessionId)
            .ToDictionary(g => g.Key, g => g.First().Title);

        // Mốc CHẤM của buổi = max(created_at) các dòng điểm của buổi — CÙNG mốc mà `progress[]` của
        // báo cáo lộ trình dùng, để hai màn hình không nói hai mốc thời gian khác nhau cho cùng buổi.
        var scoredAtBySession = scores
            .GroupBy(sc => sc.SessionId)
            .ToDictionary(g => g.Key, g => g.Max(sc => sc.CreatedAt));

        return scores
            .GroupBy(sc => sc.CriterionName)
            .Select(g =>
            {
                var sessions = g
                    .Select(sc => new MilestoneScoreSessionSnapshot(
                        sc.SessionId,
                        titleBySession.TryGetValue(sc.SessionId, out var title) ? title : string.Empty,
                        attemptNoBySession.TryGetValue(sc.SessionId, out var no) ? no : null,
                        sc.Percentage,
                        scoredAtBySession[sc.SessionId]))
                    .OrderBy(x => x.ScoredAt).ThenBy(x => x.SessionId)
                    .ToList();

                return new MilestoneScoreCriterionSnapshot(
                    g.Key,
                    // Trung bình của ĐÚNG danh sách vừa dựng ⇒ "phần tính cộng ra con số" là bất biến
                    // theo cấu trúc, không phải nhờ hai chỗ tình cờ giống nhau.
                    Math.Round(sessions.Average(x => x.Percentage), 2),
                    sessions,
                    ReferenceAveragePercentage: null,
                    ReferenceSessions: [],
                    DeltaPct: null);
            })
            .OrderBy(c => c.Name, StringComparer.Ordinal)
            .ToList();
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
        // Nguồn CHÍNH: roadmap_lesson_attempts — MỌI lần làm của mọi bài, kể cả các lần làm lại.
        // Đọc qua `lesson.session_id` (1–1, chỉ giữ lần MỚI NHẤT) sẽ làm các lần trước biến mất khỏi
        // đường xu hướng, đúng thứ mà việc luyện lại sinh ra để cho người học thấy.
        var attempts = await _db.RoadmapLessonAttempts.AsNoTracking()
            .Where(a => a.Lesson.Milestone.RoadmapId == roadmapId)
            .OrderBy(a => a.Lesson.Milestone.OrderNo).ThenBy(a => a.Lesson.OrderNo).ThenBy(a => a.AttemptNo)
            .Select(a => new { a.SessionId, a.Lesson.Title })
            .ToListAsync(ct);

        // Nguồn DỰ PHÒNG: buổi gắn thẳng vào lesson mà chưa có dòng attempt nào trỏ tới. Backfill của
        // migration đã sinh đủ các dòng đó, nên trong thực tế tập này rỗng — giữ lại vì cái giá của
        // một lỗ hổng backfill là MẤT một buổi đã hoàn thành khỏi báo cáo, im lặng, và người học
        // không có cách nào biết. Hợp (không phải thay thế) nên vẫn quan sát được lần làm lại.
        var covered = attempts.Select(x => x.SessionId).ToHashSet();
        var orphanLessons = await _db.RoadmapLessons.AsNoTracking()
            .Where(l => l.Milestone.RoadmapId == roadmapId && l.SessionId != null)
            .OrderBy(l => l.Milestone.OrderNo).ThenBy(l => l.OrderNo)
            .Select(l => new { SessionId = l.SessionId!.Value, l.Title })
            .ToListAsync(ct);

        var lessons = attempts
            .Concat(orphanLessons.Where(l => !covered.Contains(l.SessionId)))
            .ToList();
        if (lessons.Count == 0) return [];

        var sessionIds = lessons.Select(x => x.SessionId).Distinct().ToList();
        var scores = await _db.SessionCriterionScores.AsNoTracking()
            .Where(sc => sessionIds.Contains(sc.SessionId))
            .ToListAsync(ct);
        if (scores.Count == 0) return [];

        // 1 buổi ↔ 1 LẦN LÀM (UNIQUE(session_id) trên roadmap_lesson_attempts), nhưng một bài có
        // nhiều lần làm ⇒ nhiều buổi cùng mang MỘT tên bài — đó là ý đồ: đường xu hướng hiện nhiều
        // điểm cùng tên, cho thấy chính bài đó đã khá lên. Vẫn chốt deterministic (dòng đầu tiên)
        // vì không ràng buộc nào chặn hai lesson trỏ chung 1 session ở nguồn dự phòng.
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
