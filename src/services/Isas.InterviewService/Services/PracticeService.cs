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

    private readonly InterviewDbContext _db;
    private readonly IStorageService _storage;
    private readonly IAiServiceQuestionGenerator _questionGenerator;
    private readonly ISessionScoringNotifier _scoringNotifier;
    private readonly ILogger<PracticeService> _logger;

    public PracticeService(
        InterviewDbContext db,
        IStorageService storage,
        IAiServiceQuestionGenerator questionGenerator,
        ISessionScoringNotifier scoringNotifier,
        ILogger<PracticeService> logger)
    {
        _db = db;
        _storage = storage;
        _questionGenerator = questionGenerator;
        _scoringNotifier = scoringNotifier;
        _logger = logger;
    }

    // ── CREATE: tạo session + sinh câu hỏi (1 call) ───────────────────────
    public async Task<PracticeSessionResponse> CreateSessionAsync(
        Guid candidateId, CreatePracticeSessionRequest request, CancellationToken ct = default)
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

        // Tạo session, commit #1. Status set bằng C# initializer của entity.
        var session = new PracticeSession
        {
            Id = Guid.NewGuid(),
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
        // gì thì sinh câu hỏi chung theo JobCategory.
        List<GeneratedQuestion> generated;
        try
        {
            generated = await _questionGenerator.GenerateQuestionsAsync(
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
            throw new InvalidOperationException("Sinh câu hỏi thất bại", ex);
        }

        if (generated is null || generated.Count == 0)
        {
            session.Status = SessionStatus.Failed;
            await _db.SaveChangesAsync(ct);
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
            CreatedAt = DateTime.UtcNow
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

        var hasAnswer = await _db.PracticeAnswers.AnyAsync(a => a.SessionId == sessionId, ct);
        if (!hasAnswer)
            throw new InvalidOperationException("Chưa trả lời câu nào, không thể nộp");

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
        var criterionScores = session.Status == SessionStatus.Scored && session.CampaignId is null
            ? await _db.SessionCriterionScores.AsNoTracking()
                .Where(x => x.SessionId == sessionId)
                .ToListAsync(ct)
            : new List<SessionCriterionScore>();

        return MapToResponse(session, questions, answers, criterionScores);
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
    private static PracticeSessionResponse MapToResponse(
        PracticeSession s, List<PracticeQuestion> questions, List<PracticeAnswer> answers,
        IReadOnlyList<SessionCriterionScore>? criterionScores = null)
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
            MapResult(s, questions.Count, criterionScores));
    }

    // BC9: dựng tổng kết buổi từ DB. Chỉ trả khi B2C đã Scored & có breakdown; ngược lại null.
    private static SessionResultResponse? MapResult(
        PracticeSession s, int totalQuestions, IReadOnlyList<SessionCriterionScore>? criterionScores)
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

        return new SessionResultResponse(
            s.OverallScore ?? 0m,
            s.AnsweredCount ?? 0,
            totalQuestions,
            criteria,
            needsImprovement);
    }

    private static AnswerResponse MapAnswer(PracticeAnswer a)
    {
        // Mỗi tiêu chí lấy attempt mới nhất (self-consistency sau này -> nhiều attempt).
        var latestScores = a.Scores
            .GroupBy(sc => sc.CriterionId)
            .Select(g => g.OrderByDescending(sc => sc.AttemptNo).First())
            .Select(sc => new AnswerScoreResponse(
                sc.CriterionId, sc.Score, sc.Reasoning, sc.RubricVersion))
            .ToList();

        return new AnswerResponse(
            a.Id, a.Status.ToString(), a.DurationSec, a.Transcript, latestScores);
    }
}