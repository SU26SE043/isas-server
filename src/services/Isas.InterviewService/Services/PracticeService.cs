using Isas.InterviewService.ApplicationDbContext;
using Isas.InterviewService.DTOs;
using Isas.InterviewService.Entities;
using Isas.InterviewService.Enums;
using Isas.InterviewService.Services.Interfaces;
using Microsoft.EntityFrameworkCore;

namespace Isas.InterviewService.Services;

public class PracticeService : IPracticeService
{
    private const int DefaultTimeLimitSec = 120; // TODO: chỉnh nếu Gemini trả kèm

    private const string GenerationFailedReason = "generation_failed"; // BK12

    private readonly InterviewDbContext _db;
    private readonly IStorageService _storage;
    private readonly IAiServiceQuestionGenerator _questionGenerator;
    private readonly ISessionScoringNotifier _scoringNotifier;
    private readonly ICreditReservationClient _reservationClient;   // BC2
    private readonly ISessionEventPublisher _eventPublisher;        // BK12
    private readonly ILogger<PracticeService> _logger;

    public PracticeService(
        InterviewDbContext db,
        IStorageService storage,
        IAiServiceQuestionGenerator questionGenerator,
        ISessionScoringNotifier scoringNotifier,
        ICreditReservationClient reservationClient,
        ISessionEventPublisher eventPublisher,
        ILogger<PracticeService> logger)
    {
        _db = db;
        _storage = storage;
        _questionGenerator = questionGenerator;
        _scoringNotifier = scoringNotifier;
        _reservationClient = reservationClient;
        _eventPublisher = eventPublisher;
        _logger = logger;
    }

    // ── CREATE: tạo session + sinh câu hỏi (1 call) ───────────────────────
    public Task<PracticeSessionResponse> CreateSessionAsync(
        Guid candidateId, CreatePracticeSessionRequest request, CancellationToken ct = default)
        => CreateSessionInternalAsync(candidateId, request, Guid.NewGuid(), focusCriteria: null, ct);

    // BC14 — /start roadmap lesson: sessionId do caller cấp (link lesson sau khi tạo → thoả FK
    // roadmap_lessons.session_id) + câu hỏi bám focusCriteria của milestone. Reserve/gen/BK12 giữ nguyên.
    public Task<PracticeSessionResponse> CreateLessonSessionAsync(
        Guid candidateId, CreatePracticeSessionRequest request, Guid sessionId,
        IReadOnlyList<string>? focusCriteria, CancellationToken ct = default)
        => CreateSessionInternalAsync(candidateId, request, sessionId, focusCriteria, ct);

    // Lõi dùng chung cho CreateSessionAsync (sessionId ngẫu nhiên, không focus) và
    // CreateLessonSessionAsync (sessionId caller cấp + focusCriteria roadmap lesson).
    private async Task<PracticeSessionResponse> CreateSessionInternalAsync(
        Guid candidateId, CreatePracticeSessionRequest request, Guid sessionId,
        IReadOnlyList<string>? focusCriteria, CancellationToken ct)
    {
        // CV optional: chỉ parse khi có. Không có CV cũng luyện được (dựa JobCategory).
        // TODO: xác nhận tên method storage (memory ghi GetParseText).
        string? cvText = null;
        if (request.CvId is not null)
        {
            cvText = await _storage.GetParseTextAsync(request.CvId.Value, ct);
            if (string.IsNullOrWhiteSpace(cvText))
                throw new InvalidOperationException("CV không đọc được nội dung");
        }

        // JD optional: chỉ parse khi có.
        string? jdText = null;
        if (request.JdId is not null)
        {
            jdText = await _storage.GetParseTextAsync(request.JdId.Value, ct);
            if (string.IsNullOrWhiteSpace(jdText))
                throw new InvalidOperationException("JD không đọc được nội dung");
        }

        // BC2: reserve 1 credit ví cá nhân (owner=User) TRƯỚC khi tạo session row.
        // sessionId cấp trước → reserve khoá idempotency theo đúng Id session sẽ dùng (P4).
        // Ví hết credit → Payment 402 → InsufficientCreditException ném ở đây ⇒ KHÔNG có row session (PAY-5).
        // (AI sinh câu hỏi lỗi SAU reserve → session Failed nhưng credit đã giữ; BC4 release khi Abandoned/Failed.)
        var reservation = await _reservationClient.ReserveAsync(
            ownerType: "User", ownerId: candidateId, sessionId: sessionId, ct: ct);
        _logger.LogInformation(
            "Reserve credit ví cá nhân cho session {SessionId} (candidate {CandidateId}, reservation {ReservationId})",
            sessionId, candidateId, reservation.ReservationId);

        // Tạo session, commit #1. Status set bằng C# initializer của entity.
        var session = new PracticeSession
        {
            Id = sessionId,
            CandidateId = candidateId,
            CvId = request.CvId,           // có thể null
            JdId = request.JdId,           // có thể null
            JobCategory = request.JobCategory,
            Status = SessionStatus.GeneratingQuestions,
            CreatedAt = DateTime.UtcNow
        };
        _db.PracticeSessions.Add(session);
        await _db.SaveChangesAsync(ct);

        // Gọi Gemini NGOÀI transaction — không giữ DB connection lúc chờ AI.
        // Prompt tự xử 3 kịch bản: có JD ưu tiên JD; chỉ CV thì bám CV; không có
        // gì thì sinh câu hỏi chung theo JobCategory. focusCriteria (lesson /start) đưa thêm để bám tiêu chí.
        List<GeneratedQuestion> generated;
        try
        {
            // focusCriteria chỉ có ở lesson /start (BC14) → dùng overload mang focusCriteria; luồng
            // thường (null/rỗng) giữ nguyên overload cũ (không đổi hành vi/không đổi hợp đồng mock cũ).
            generated = focusCriteria is { Count: > 0 }
                ? await _questionGenerator.GenerateQuestionsAsync(
                    session.JobCategory.ToString(), cvText, jdText, focusCriteria, ct)
                : await _questionGenerator.GenerateQuestionsAsync(
                    jobCategory: session.JobCategory.ToString(),
                    cvText: cvText,            // null nếu không có
                    jdText: jdText,            // null nếu không có
                    ct: ct);
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Sinh câu hỏi lỗi cho session {SessionId}", session.Id);
            session.Status = SessionStatus.Failed;
            await _db.SaveChangesAsync(ct);
            await PublishGenerationFailedAbandonAsync(session, ct);   // BK12: release credit đã reserve
            throw new InvalidOperationException("Sinh câu hỏi thất bại", ex);
        }

        if (generated is null || generated.Count == 0)
        {
            session.Status = SessionStatus.Failed;
            await _db.SaveChangesAsync(ct);
            await PublishGenerationFailedAbandonAsync(session, ct);   // BK12: release credit đã reserve
            throw new InvalidOperationException("AIService không trả về câu hỏi nào");
        }

        // Lưu câu hỏi + set Ready, commit #2 (tách commit tránh concurrency).
        var questions = generated
            .Select((q, idx) => new PracticeQuestion
            {
                Id = Guid.NewGuid(),
                SessionId = session.Id,
                OrderNo = idx + 1,
                Content = q.Content,
                TimeLimitSec = DefaultTimeLimitSec
            })
            .ToList();

        _db.PracticeQuestions.AddRange(questions);
        session.Status = SessionStatus.Ready;
        await _db.SaveChangesAsync(ct);

        _logger.LogInformation(
            "Tạo session {SessionId} ({Cat}) với {Count} câu hỏi (cv={HasCv}, jd={HasJd})",
            session.Id, session.JobCategory, questions.Count,
            cvText != null, jdText != null);

        return MapToResponse(session, questions, new List<PracticeAnswer>());
    }

    // ── CREATE B2B: session gắn campaign_id + materialize tiêu chí campaign (I1) ──────
    // Câu hỏi + tiêu chí do Campaign cấp sẵn (không gọi AI sinh). rubric_criteria keyed by
    // campaign_id → dùng chung mọi session của campaign ⇒ materialize idempotent theo campaign.
    public async Task<PracticeSessionResponse> CreateCampaignSessionAsync(
        Guid candidateId, CreateCampaignSessionRequest request, CancellationToken ct = default)
    {
        if (request.Questions is null || request.Questions.Count == 0)
            throw new InvalidOperationException("Campaign session cần ít nhất 1 câu hỏi");
        if (request.Criteria is null || request.Criteria.Count == 0)
            throw new InvalidOperationException("Campaign session cần ít nhất 1 tiêu chí");

        var session = new PracticeSession
        {
            Id = Guid.NewGuid(),
            CandidateId = candidateId,
            CampaignId = request.CampaignId,
            JobCategory = request.JobCategory,
            Status = SessionStatus.Ready,   // câu hỏi cấp sẵn → không cần sinh AI
            CreatedAt = DateTime.UtcNow,
            Deadline = request.ExpiresAt    // I2: hạn chót nhận bài (B2B); null → không hard-deadline
        };
        _db.PracticeSessions.Add(session);

        var questions = request.Questions
            .Select((content, idx) => new PracticeQuestion
            {
                Id = Guid.NewGuid(),
                SessionId = session.Id,
                OrderNo = idx + 1,
                Content = content,
                TimeLimitSec = DefaultTimeLimitSec
            })
            .ToList();
        _db.PracticeQuestions.AddRange(questions);

        // Materialize tiêu chí campaign → rubric_criteria(campaign_id), idempotent theo campaign.
        var alreadyMaterialized = await _db.RubricCriteria
            .AnyAsync(c => c.CampaignId == request.CampaignId, ct);
        if (!alreadyMaterialized)
        {
            var criteria = request.Criteria.Select(c => new RubricCriterion
            {
                Id = Guid.NewGuid(),
                Name = c.Name,
                Description = c.Description,
                Weight = c.Weight,
                MaxScore = c.MaxScore,
                IsActive = true,
                JobCategory = request.JobCategory,   // cột bắt buộc; B2B chấm theo campaign_id
                CampaignId = request.CampaignId,
                Version = 1
            });
            _db.RubricCriteria.AddRange(criteria);
        }

        await _db.SaveChangesAsync(ct);

        _logger.LogInformation(
            "Tạo session B2B {SessionId} cho campaign {CampaignId} ({Q} câu, materialize criteria={Mat})",
            session.Id, request.CampaignId, questions.Count, !alreadyMaterialized);

        return MapToResponse(session, questions, new List<PracticeAnswer>());
    }

    // ── CREATE-OR-GET B2B (D2): idempotent theo (candidateId, campaignId) ─────────────
    // Campaign /start có thể gọi nhiều lần (ứng viên refresh / bấm lại) — trả CÙNG session đang mở
    // thay vì đẻ session mới. "Đang mở" = chưa terminal (Scored/Failed/SessionAbandoned). Hết mở →
    // tạo session mới (I1). KHÔNG dùng UNIQUE DB (race hiếm chấp nhận được ở scope này) — dedup bằng query.
    public async Task<PracticeSessionResponse> GetOrCreateCampaignSessionAsync(
        Guid candidateId, CreateCampaignSessionRequest request, CancellationToken ct = default)
    {
        var existing = await _db.PracticeSessions
            .FirstOrDefaultAsync(s =>
                s.CandidateId == candidateId
                && s.CampaignId == request.CampaignId
                && s.Status != SessionStatus.Scored
                && s.Status != SessionStatus.Failed
                && s.Status != SessionStatus.SessionAbandoned, ct);

        if (existing is null)
            return await CreateCampaignSessionAsync(candidateId, request, ct);

        var questions = await _db.PracticeQuestions.AsNoTracking()
            .Where(q => q.SessionId == existing.Id)
            .OrderBy(q => q.OrderNo)
            .ToListAsync(ct);

        var answers = await _db.PracticeAnswers.AsNoTracking()
            .Include(a => a.Scores)
            .Where(a => a.SessionId == existing.Id)
            .ToListAsync(ct);

        _logger.LogInformation(
            "create-or-get: trả session B2B đang mở {SessionId} (candidate {CandidateId}, campaign {CampaignId})",
            existing.Id, candidateId, request.CampaignId);

        return MapToResponse(existing, questions, answers);
    }

    // ── SUBMIT SESSION: chốt sổ (KHÔNG publish — chấm dần đã publish lúc upload) ──
    public async Task SubmitSessionAsync(
        Guid candidateId, Guid sessionId, CancellationToken ct = default)
    {
        var session = await _db.PracticeSessions
            .FirstOrDefaultAsync(s => s.Id == sessionId, ct)
            ?? throw new KeyNotFoundException("Session không tồn tại");

        if (session.CandidateId != candidateId)
            throw new UnauthorizedAccessException("Không phải buổi của bạn");

        if (session.Status is not (SessionStatus.Ready or SessionStatus.InProgress))
            throw new InvalidOperationException(
                $"Buổi ở trạng thái {session.Status}, không thể nộp");

        // INT-5: cần ≥1 câu trả lời THẬT mới nộp được (đếm trước khi tạo Skipped bên dưới).
        var hasAnswer = await _db.PracticeAnswers.AnyAsync(a => a.SessionId == sessionId, ct);
        if (!hasAnswer)
            throw new InvalidOperationException("Chưa trả lời câu nào, không thể nộp");

        // I2 (D21): chốt buổi theo TỪNG CÂU — câu CHƯA có answer → đánh `Skipped` (không chặn đóng buổi;
        // câu có audio giữ nguyên trạng thái đang chấm). Skipped tính là "done" ở allDone bên dưới.
        await MarkUnansweredAsSkippedAsync(sessionId, ct);

        // Chấm dần: mỗi answer đã được publish ngay lúc upload (AnswerService).
        // SubmitSession chỉ chốt sổ — KHÔNG publish lại để tránh chấm trùng.
        session.Status = SessionStatus.Scoring;
        session.CompletedAt = DateTime.UtcNow;
        await _db.SaveChangesAsync(ct);

        // Race của chấm dần: answer cuối có thể đã Scored TRƯỚC khi user bấm submit,
        // khi đó callback không đóng session (lúc đó session còn InProgress).
        // Phải kiểm tra ngay để đóng, tránh session kẹt Scoring vì không còn callback.
        var statuses = await _db.PracticeAnswers
            .Where(a => a.SessionId == sessionId)
            .Select(a => a.Status)
            .ToListAsync(ct);

        bool allDone = statuses.All(s =>
            s is AnswerStatus.Scored or AnswerStatus.Skipped or AnswerStatus.Failed);

        if (allDone)
        {
            session.Status = SessionStatus.Scored;
            await _db.SaveChangesAsync(ct);
            _logger.LogInformation(
                "Session {SessionId} -> Scored ngay khi submit (đã chấm xong từ trước)", sessionId);

            // E2: phát SessionScored (campaign_id + điểm tổng) khi session vừa đóng.
            await _scoringNotifier.NotifySessionScoredAsync(sessionId, ct);
        }
        else
        {
            _logger.LogInformation("Chốt session {SessionId} -> Scoring (đang chờ chấm nốt)", sessionId);
        }
    }

    // I2 (D21) per-question finalize: mọi câu của buổi CHƯA có answer → tạo answer `Skipped`
    // (không audio, DurationSec=0). Dùng khi chốt buổi (manual submit + sweeper auto-submit) để câu
    // trống không kẹt buổi ở Scoring. Câu đã có answer (Uploaded/Scoring/Scored/Failed) KHÔNG đụng.
    // Add vào context (KHÔNG SaveChanges) — caller lưu chung trong lần SaveChanges chốt buổi.
    private async Task MarkUnansweredAsSkippedAsync(Guid sessionId, CancellationToken ct)
    {
        var answeredQuestionIds = await _db.PracticeAnswers
            .Where(a => a.SessionId == sessionId)
            .Select(a => a.QuestionId)
            .ToListAsync(ct);

        var unansweredQuestionIds = await _db.PracticeQuestions
            .Where(q => q.SessionId == sessionId && !answeredQuestionIds.Contains(q.Id))
            .Select(q => q.Id)
            .ToListAsync(ct);

        if (unansweredQuestionIds.Count == 0) return;

        var now = DateTime.UtcNow;
        _db.PracticeAnswers.AddRange(unansweredQuestionIds.Select(qid => new PracticeAnswer
        {
            Id = Guid.NewGuid(),
            SessionId = sessionId,
            QuestionId = qid,
            Status = AnswerStatus.Skipped,
            DurationSec = 0,
            CreatedAt = now
        }));

        _logger.LogInformation(
            "Chốt buổi {SessionId}: đánh {Count} câu chưa trả lời là Skipped", sessionId, unansweredQuestionIds.Count);
    }

    // ── GET ───────────────────────────────────────────────────────────────
    public async Task<PracticeSessionResponse?> GetSessionAsync(
        Guid candidateId, Guid sessionId, CancellationToken ct = default)
    {
        var session = await _db.PracticeSessions
            .AsNoTracking()
            .FirstOrDefaultAsync(s => s.Id == sessionId, ct);

        if (session is null) return null;
        if (session.CandidateId != candidateId)
            throw new UnauthorizedAccessException("Không phải buổi của bạn");

        var questions = await _db.PracticeQuestions
            .AsNoTracking()
            .Where(q => q.SessionId == sessionId)
            .OrderBy(q => q.OrderNo)
            .ToListAsync(ct);

        var answers = await _db.PracticeAnswers
            .AsNoTracking()
            .Include(a => a.Scores)
            .Where(a => a.SessionId == sessionId)
            .ToListAsync(ct);

        // BC9: tổng kết buổi chỉ áp B2C đã Scored — đọc thẳng breakdown từ DB (không tính lại).
        var isB2CScored = session.Status == SessionStatus.Scored && session.CampaignId is null;
        var criterionScores = isB2CScored
            ? await _db.SessionCriterionScores.AsNoTracking()
                .Where(x => x.SessionId == sessionId)
                .ToListAsync(ct)
            : new List<SessionCriterionScore>();

        // BC8: đối chiếu CV↔trả lời — chỉ B2C đã Scored & có CV đã phân tích (BC7). ĐỌC dữ liệu sẵn
        // có (không AI): lấy phân tích CV mới nhất cho đúng CvId của buổi (join lỏng qua CvId+chủ).
        IReadOnlyList<string> cvStrengths = Array.Empty<string>();
        if (isB2CScored && session.CvId is not null)
        {
            var cv = await _db.CvAnalyses.AsNoTracking()
                .Where(x => x.CvId == session.CvId && x.CandidateId == session.CandidateId)
                .OrderByDescending(x => x.CreatedAt)
                .FirstOrDefaultAsync(ct);
            if (cv is not null)
                cvStrengths = MergeStrengths(cv);
        }

        return MapToResponse(session, questions, answers, criterionScores, cvStrengths);
    }

    // ── HISTORY ───────────────────────────────────────────────────────────
    public async Task<IReadOnlyList<PracticeSessionSummary>> GetHistoryAsync(
        Guid candidateId, CancellationToken ct = default)
    {
        return await _db.PracticeSessions
            .AsNoTracking()
            .Where(s => s.CandidateId == candidateId)
            .OrderByDescending(s => s.CreatedAt)
            .Select(s => new PracticeSessionSummary(
                s.Id, s.Status.ToString(), s.JobCategory.ToString(),
                s.CreatedAt, s.CompletedAt, s.OverallScore))   // BC9: lịch sử hiện điểm tổng
            .ToListAsync(ct);
    }

    // ── helpers ───────────────────────────────────────────────────────────

    // BK12: B2C reserve credit ví cá nhân (BC2) TRƯỚC khi sinh câu hỏi. Nếu AI sinh câu hỏi lỗi →
    // session `Failed`, credit đang bị KẸT: E3 sweeper chỉ quét `InProgress`, còn `Failed` KHÔNG tự
    // phát `SessionAbandoned` → E7 (Payment) không release → orphan credit. Fix: phát
    // `SessionAbandoned(reason=generation_failed)` để E7 hoàn credit ví User.
    // Best-effort (nuốt lỗi publish): session đã `Failed` trong DB rồi, publish lỗi KHÔNG được chặn
    // luồng (đồng pattern nuốt lỗi ở SessionScoringNotifier/SessionAbandonSweeper). E7 release
    // absorbing (reservation không tồn tại/đã finalized → no-op) nên phát cho session không có
    // reservation cũng an toàn. Chỉ B2C dùng path này (CreateSessionAsync); B2B không reserve (PAY-6)
    // và không có nhánh Failed-sau-reserve.
    private async Task PublishGenerationFailedAbandonAsync(PracticeSession session, CancellationToken ct)
    {
        var evt = new SessionAbandonedEvent
        {
            SessionId = session.Id,
            CampaignId = session.CampaignId,   // null cho B2C
            CandidateId = session.CandidateId,
            Reason = GenerationFailedReason,
            AbandonedAt = DateTime.UtcNow
        };

        try
        {
            await _eventPublisher.PublishSessionAbandonedAsync(evt, ct);
            _logger.LogInformation(
                "BK12: phát SessionAbandoned(generation_failed) cho session {SessionId} để release credit ví User",
                session.Id);
        }
        catch (Exception ex)
        {
            _logger.LogError(ex,
                "BK12: phát SessionAbandoned(generation_failed) thất bại cho session {SessionId}", session.Id);
        }

        // BC14 (defense-in-depth): nếu session này đang gắn 1 roadmap lesson (Practicing) mà sinh câu
        // hỏi lỗi → trả lesson về Theory + clear session_id để /start lại được. Luồng /start hiện link
        // lesson SAU khi tạo session xong (FK), nên gen-fail thường CHƯA link → no-op; giữ để an toàn
        // nếu thứ tự đổi. Best-effort (nuốt lỗi — session đã Failed trong DB).
        await RevertLinkedLessonAsync(session.Id, ct);
    }

    // BC14 — reset lesson đang gắn 1 session không-Scored về Theory (start lại được). Guard theo
    // session_id + status Practicing → chỉ chạm lesson đang luyện đúng session này (no-op nếu không có).
    private async Task RevertLinkedLessonAsync(Guid sessionId, CancellationToken ct)
    {
        try
        {
            await _db.RoadmapLessons
                .Where(l => l.SessionId == sessionId && l.Status == LessonStatus.Practicing)
                .ExecuteUpdateAsync(u => u
                    .SetProperty(l => l.Status, LessonStatus.Theory)
                    .SetProperty(l => l.SessionId, (Guid?)null), ct);
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "BC14: revert lesson về Theory thất bại cho session {SessionId}", sessionId);
        }
    }

    private static PracticeSessionResponse MapToResponse(
        PracticeSession s, List<PracticeQuestion> questions, List<PracticeAnswer> answers,
        IReadOnlyList<SessionCriterionScore>? criterionScores = null,
        IReadOnlyList<string>? cvStrengths = null)
    {
        var answerByQuestion = answers.ToDictionary(a => a.QuestionId);

        var qResponses = questions
            .OrderBy(q => q.OrderNo)
            .Select(q => new QuestionResponse(
                q.Id, q.OrderNo, q.Content, q.TimeLimitSec,
                answerByQuestion.TryGetValue(q.Id, out var a) ? MapAnswer(a) : null))
            .ToList();

        return new PracticeSessionResponse(
            s.Id, s.Status.ToString(), s.JobCategory.ToString(),
            s.CvId, s.JdId, s.CreatedAt, s.CompletedAt, qResponses,
            MapResult(s, questions.Count, criterionScores, cvStrengths));
    }

    // BC9: dựng tổng kết buổi từ DB. Chỉ trả khi B2C đã Scored & có breakdown; ngược lại null.
    private static SessionResultResponse? MapResult(
        PracticeSession s, int totalQuestions, IReadOnlyList<SessionCriterionScore>? criterionScores,
        IReadOnlyList<string>? cvStrengths = null)
    {
        if (s.Status != SessionStatus.Scored || s.CampaignId is not null
            || criterionScores is not { Count: > 0 })
            return null;

        var criteria = criterionScores
            .Select(cs => new CriterionScoreResponse(
                cs.CriterionId, cs.CriterionName, cs.AverageScore, cs.MaxScore, cs.Percentage, cs.Weight))
            .ToList();

        var needsImprovement = criterionScores
            .Where(cs => cs.NeedsImprovement)
            .Select(cs => cs.CriterionId)
            .ToList();

        // BC8: mục "CV vs câu trả lời" — null nếu buổi không có CV đã phân tích (cvStrengths rỗng).
        var cvVsAnswer = CvVsAnswerReportBuilder.Build(cvStrengths ?? Array.Empty<string>(), criterionScores);

        return new SessionResultResponse(
            s.OverallScore ?? 0m,
            s.AnsweredCount ?? 0,
            totalQuestions,
            criteria,
            needsImprovement,
            OverallComment: s.OverallComment,   // BC10 — nhận xét chung (AI, best-effort); null nếu chưa/AI lỗi.
            CvVsAnswer: cvVsAnswer);
    }

    // BC8: gộp tín hiệu "CV mạnh" = strengths + matched skills (nếu có JD match), khử trùng giữ thứ tự.
    private static IReadOnlyList<string> MergeStrengths(CvAnalysis cv)
    {
        var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        var merged = new List<string>();
        foreach (var s in cv.Strengths.Concat(cv.JdMatch?.MatchedSkills ?? Enumerable.Empty<string>()))
        {
            var v = s?.Trim();
            if (string.IsNullOrEmpty(v)) continue;
            if (seen.Add(v)) merged.Add(v);
        }
        return merged;
    }

    private static AnswerResponse MapAnswer(PracticeAnswer a)
    {
        // Mỗi tiêu chí lấy attempt mới nhất (self-consistency sau này -> nhiều attempt).
        var latestScores = a.Scores
            .GroupBy(sc => sc.CriterionId)
            .Select(g => g.OrderByDescending(sc => sc.AttemptNo).First())
            .Select(sc => new AnswerScoreResponse(
                sc.CriterionId, sc.Score, sc.Reasoning, sc.RubricVersion, sc.LevelMatched))
            .ToList();

        return new AnswerResponse(
            a.Id, a.Status.ToString(), a.DurationSec, a.Transcript, latestScores);
    }
}