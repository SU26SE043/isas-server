using Isas.InterviewService.ApplicationDbContext;
using Isas.InterviewService.DTOs;
using Isas.InterviewService.Entities;
using Isas.InterviewService.Enums;
using Isas.InterviewService.Services.Interfaces;
using Isas.Shared.Scoring;
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

        var (totalScore, scoringInputs, scoreFallback) = await ComputeScoreAndInputsAsync(session, ct);

        var evt = new SessionScoredEvent
        {
            SessionId = session.Id,
            CampaignId = session.CampaignId,
            CandidateId = session.CandidateId,
            TotalScore = totalScore,
            ScoredAt = DateTime.UtcNow,
            // Nhãn thước đo cho bảng xếp hạng (CAMP-10) — xem SessionScoredEvent.RubricVersion.
            RubricVersion = session.CampaignRubricVersion,
            // SCP1 · B5 — bó biến RAW để B8 tính lại điểm bằng chính sách biểu thức. null nếu chưa
            // chấm được tiêu chí nào (session bỏ ngang đường này không phát SessionScored, nhưng
            // giữ null-safe).
            ScoringInputs = scoringInputs,
            // SCP1 · B6 / HĐ-5 — cờ RIÊNG: true = biểu thức chính sách LỖI lúc chạy trên buổi này ⇒
            // điểm tính bằng công thức mặc định. Bảng kết quả (HĐ-5) phải hiện được, nếu không đây lại
            // là một thứ hỏng im lặng.
            ScoreFallback = scoreFallback
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

            var comment = language == "vi"
                ? await _summarizer.SummarizeAsync(jobCategory.ToString(), overall, criteria, ct)
                : await _summarizer.SummarizeAsync(jobCategory.ToString(), overall, criteria, language, ct);
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
    //
    // SCP1 · B5 — trả kèm BÓ BIẾN THÔ per-criterion (name/pct/weight/maxScore) + answered/totalQuestions.
    // Dựng từ CÙNG `criteria` + `scores` đang tính điểm ⇒ không thêm query nào ngoài 2 CountAsync.
    // Lưu RAW, KHÔNG lưu scalar đã tính (weighted_avg_pct…) — CẤM #3: append thêm biến sau này thì
    // hàng lịch sử vẫn tính lại được (B8).
    //
    // SCP1 · B6 — nếu buổi ĐÃ GHIM chính sách (campaign_policy_expression), điểm = đánh giá biểu thức
    // đó trên bó biến RAW; lỗi lúc chạy ⇒ LÙI về công thức weighted mặc định + cờ scoreFallback.
    // `weighted_avg_pct` giữ NGUYÊN công thức hiện tại — nó chỉ trở thành MỘT BIẾN (B1), không bị thay.
    private async Task<(decimal Total, ScoringInputsSnapshot? Inputs, bool ScoreFallback)> ComputeScoreAndInputsAsync(
        PracticeSession session, CancellationToken ct)
    {
        var sessionId = session.Id;

        // Nguồn tiêu chí đi qua ĐÚNG loader mà đường chấm đã dùng (E1/BC16 + ghim phiên bản B2B).
        //
        // Trước đây chỗ này tự viết lại câu truy vấn với `is_active`. Từ khi B2B có versioning, đó là
        // một cái bẫy tiền: buổi ghim v1 mà campaign đã bump v2 (v1 bị hạ cờ) ⇒ nạp về bộ v2, trong
        // khi answer_scores mang criterion_id của v1 ⇒ hai tập ID KHÔNG GIAO NHAU ⇒ mọi vòng lặp
        // `continue` ⇒ weightSum = 0 ⇒ event mang TotalScore = 0 ⇒ ứng viên bị xếp hạng bằng ĐIỂM 0
        // trong khi bài của họ đã được chấm đầy đủ. Đúng hình dạng lỗi Q8 mô tả ở nhánh B2C bên dưới,
        // chỉ khác lối vào.
        //
        // `includeLevels: false` — chỗ này chỉ cần weight/maxScore để cộng điểm; không đổi TẬP dòng.
        //
        // DB30/Q8 (nhánh B2C, giữ nguyên qua loader): thiếu predicate candidate_id ⇒ nạp rubric RIÊNG
        // của MỌI candidate cùng nghề; và resolver từng hard-code "vi" trong khi đường chấm resolve
        // theo session.Language ⇒ ứng viên có rubric riêng + buổi "en" cho ra hai bộ tiêu chí khác
        // hẳn, giao ID rỗng, TotalScore = 0. Cả hai vế nay nằm trong loader dùng chung.
        var criteria = await RubricCriteriaLoader.LoadAsync(
            _db, RubricCriteriaLoader.KeyFor(session), ct, includeLevels: false);
        if (criteria.Count == 0) return (0m, null, false);

        var scores = await _db.AnswerScores
            .AsNoTracking()
            .Where(sc => sc.Answer.SessionId == sessionId)
            .Select(sc => new { sc.AnswerId, sc.CriterionId, sc.Score })
            .ToListAsync(ct);
        if (scores.Count == 0) return (0m, null, false);

        var answered = await _db.PracticeAnswers.CountAsync(a => a.SessionId == sessionId, ct);
        var totalQuestions = await _db.PracticeQuestions.CountAsync(q => q.SessionId == sessionId, ct);

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
        // Bó biến THÔ per-criterion — chỉ những tiêu chí THỰC SỰ có điểm (giống mẫu số của công thức
        // tổng: tiêu chí không ai hỏi/không có điểm rơi khỏi cả hai). B8 dựng ScoringContext từ đây.
        var bag = new List<CriterionInputSnapshot>(criteria.Count);
        foreach (var c in criteria)
        {
            if (!avgByCriterion.TryGetValue(c.Id, out var avgScore)) continue;
            var maxScore = c.MaxScore > 0 ? c.MaxScore : 1; // phòng chia 0 (ràng buộc maxScore≥1)
            var pct = Math.Clamp(avgScore / maxScore * 100m, 0m, 100m);
            weightedSum += pct * c.Weight;
            weightSum += c.Weight;
            bag.Add(new CriterionInputSnapshot(c.Name, Math.Round(pct, 4), c.Weight, c.MaxScore));
        }

        var inputs = new ScoringInputsSnapshot(bag, answered, totalQuestions);

        // Công thức MẶC ĐỊNH (weighted). Giữ NGUYÊN — nó là biến `weighted_avg_pct` (B1, append-only)
        // và là đích LÙI AN TOÀN khi biểu thức chính sách lỗi.
        var defaultTotal = weightSum <= 0m
            ? 0m
            : Math.Clamp(Math.Round(weightedSum / weightSum, 2), 0m, 100m);

        // (5) Buổi CHƯA ghim chính sách (B2C, hoặc B2B chưa áp, hoặc dữ liệu trước SCP1) → công thức mặc định.
        if (string.IsNullOrWhiteSpace(session.CampaignPolicyExpression))
            return (defaultTotal, inputs, ScoreFallback: false);

        // (4) BÁO LỖI ĐÁNH GIÁ — KHÔNG lùi an toàn, KHÔNG bịa điểm. total_questions = 0 trong khi đã
        // ghim chính sách là BẤT BIẾN HỆ THỐNG bị vi phạm (mọi buổi B2B tạo với ≥1 câu campaign), không
        // phải một cấu hình hợp lệ mà biểu thức "không may" hỏng trên đó. Ném để có người điều tra.
        if (totalQuestions <= 0)
        {
            _logger.LogError(
                "SCP1/B6: session {SessionId} đã ghim chính sách chấm (v{Ver}) nhưng total_questions = 0 "
                + "— bất biến hệ thống bị vi phạm, KHÔNG tính điểm.",
                sessionId, session.CampaignPolicyVersion);
            throw new InvalidOperationException(
                $"SCP1: session {sessionId} có total_questions = 0 với chính sách chấm đã ghim.");
        }

        // (2)+(3) Đánh giá biểu thức đã ghim trên bó biến RAW CỦA CHÍNH BUỔI NÀY. Lỗi lúc chạy — chia 0,
        // tràn số, bộ đánh giá ném, kết quả < 0 hoặc > 100 (Evaluate tự trả RESULT_OUT_OF_RANGE, KHÔNG
        // clamp) — ⇒ LÙI về `defaultTotal` + cờ RIÊNG scoreFallback. KHÔNG dùng needs_review (cờ đó đã
        // có ba nguồn khác, UI không phân biệt được lý do). KHÔNG nuốt lỗi: mọi lần lùi ghi log.
        var ctx = ScoringContext.ForInterview(inputs.ToInterviewInputs());
        decimal? policyScore = null;
        string failReason = "UNKNOWN";
        try
        {
            var parsed = ScoringExpression.Parse(session.CampaignPolicyExpression);
            if (!parsed.Ok)
                failReason = parsed.Errors.Count > 0 ? parsed.Errors[0].Code : "PARSE_ERROR";
            else
            {
                var eval = parsed.Evaluate(ctx);
                if (eval.Ok)
                    policyScore = eval.Value;   // đã trong [0,100]; ngược lại eval.Ok = false
                else
                    failReason = eval.Errors.Count > 0 ? eval.Errors[0].Code : "EVAL_ERROR";
            }
        }
        catch (OverflowException)
        {
            failReason = "OVERFLOW";
        }
        catch (Exception ex)
        {
            failReason = "ENGINE_THREW";
            _logger.LogError(ex, "SCP1/B6: bộ đánh giá ném cho session {SessionId}", sessionId);
        }

        if (policyScore is decimal ps)
        {
            _logger.LogInformation(
                "SCP1/B6: session {SessionId} chấm bằng chính sách v{Ver} = {Score}",
                sessionId, session.CampaignPolicyVersion, ps);
            return (Math.Round(ps, 2), inputs, ScoreFallback: false);
        }

        _logger.LogWarning(
            "SCP1/B6: session {SessionId} — chính sách chấm v{Ver} LỖI [{Reason}] ⇒ lùi về công thức "
            + "mặc định = {Default}, scoreFallback = true.",
            sessionId, session.CampaignPolicyVersion, failReason, defaultTotal);
        return (defaultTotal, inputs, ScoreFallback: true);
    }
}
