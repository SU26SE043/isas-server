using Isas.InterviewService.ApplicationDbContext;
using Isas.InterviewService.DTOs;
using Isas.InterviewService.Entities;
using Isas.InterviewService.Enums;
using Isas.InterviewService.Models;
using Isas.InterviewService.Services.Interfaces;
using Isas.Shared.Pagination;
using Isas.Shared.Validation;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Options;

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
    private readonly AdaptiveOptions _adaptive;   // phỏng vấn THÍCH ỨNG (B2C seed count + toggle)
    private readonly ILogger<PracticeService> _logger;

    public PracticeService(
        InterviewDbContext db,
        IStorageService storage,
        IAiServiceQuestionGenerator questionGenerator,
        ISessionScoringNotifier scoringNotifier,
        ICreditReservationClient reservationClient,
        ILogger<PracticeService> logger,
        // Optional (default null) → mọi test dựng PracticeService cũ (6 tham số) vẫn compile + adaptive tắt;
        // DI inject bản thật (Configure<AdaptiveOptions>). null → AdaptiveOptions mặc định (Enabled=false).
        IOptions<AdaptiveOptions>? adaptiveOptions = null)
    {
        _db = db;
        _storage = storage;
        _questionGenerator = questionGenerator;
        _scoringNotifier = scoringNotifier;
        _reservationClient = reservationClient;
        _adaptive = adaptiveOptions?.Value ?? new AdaptiveOptions();
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
        // jobCategory BẮT BUỘC. Guard NGAY ĐẦU (trước cả đọc CV/reserve) → thiếu → 400 (controller map
        // InvalidOperationException → BadRequest), KHÔNG giữ credit oan (PAY-5). HTTP thật cũng 400 sớm
        // hơn nhờ [Required]; test gọi service trực tiếp nên cần guard này (mẫu CvAnalysisService/BK6).
        if (request.JobCategory is null)
            throw new InvalidOperationException("jobCategory là bắt buộc.");
        var jobCategory = request.JobCategory.Value;

        // F2 — thời lượng mỗi câu. Guard TRƯỚC reserve (PAY-5): giá trị sai → 400 mà KHÔNG giữ credit oan.
        var timeLimitSec = ValidateTimeLimitSec(request.TimeLimitSec);

        // F2b — số câu. Cùng lý do đặt trước reserve: 21 câu phải bị từ chối mà không trừ credit.
        var questionCount = ValidateQuestionCount(request.QuestionCount);

        // JD nhập tay: chuẩn hoá + cap độ dài NGAY ĐẦU, TRƯỚC cả đọc CV và reserve — guard rẻ nhất
        // (thuần in-memory) chạy trước → JD quá dài → 400 mà không tốn round-trip storage và KHÔNG giữ
        // credit oan (mẫu BK6/PAY-5). Text rỗng/toàn khoảng trắng = coi như KHÔNG nhập (rơi về jdId).
        var jdTextInput = NormalizeText(request.JdText);

        // CV optional: chỉ parse khi có. Không có CV cũng luyện được (dựa JobCategory).
        // TODO: xác nhận tên method storage (memory ghi GetParseText).
        string? cvText = null;
        if (request.CvId is not null)
        {
            // Owner-scoped: file của người khác coi như không tồn tại (interview.md §Validation).
            cvText = await _storage.GetOwnedParsedTextAsync(request.CvId.Value, candidateId, ct);
            if (string.IsNullOrWhiteSpace(cvText))
                throw new InvalidOperationException("CV không đọc được nội dung");
        }

        // JD optional, 2 nguồn: text nhập thẳng (jdText) HOẶC file đã upload (jdId).
        // TEXT ƯU TIÊN FILE — quy ước C11 đã chốt bên B2B/Campaign, áp nguyên sang B2C cho nhất quán:
        // gửi cả hai thì text thắng và file bị bỏ hẳn (không parse, không lưu jd_id) → row không "nhận vơ"
        // một file thực ra không góp gì vào câu hỏi. (jdTextInput đã chuẩn hoá + kiểm ngưỡng ở đầu hàm.)
        var jdIdToUse = jdTextInput is not null ? null : request.JdId;

        string? jdText = jdTextInput;
        if (jdTextInput is null && request.JdId is not null)
        {
            jdText = await _storage.GetOwnedParsedTextAsync(request.JdId.Value, candidateId, ct);
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

        // P1-2 — TỪ ĐÂY reserve ĐÃ THÀNH CÔNG (credit đã trừ). Nếu BẤT KỲ bước sau ném (SaveChanges,
        // AI gen, lưu câu hỏi…) mà không hoàn chỗ giữ → reservation treo → credit MẤT. Bọc toàn bộ
        // hậu-reserve trong try/catch: mọi lỗi → ReleaseAsync(sessionId) best-effort (idempotent PAY-11,
        // an toàn cả khi nhánh gen-fail đã phát SessionAbandoned) TRƯỚC khi ném lại. Không đổi happy path.
        try
        {
            // Tạo session, commit #1. Status set bằng C# initializer của entity.
            var session = new PracticeSession
            {
                Id = sessionId,
                CandidateId = candidateId,
                CvId = request.CvId,           // có thể null
                JdId = jdIdToUse,              // null khi JD đến từ text (C11: text ưu tiên file)
                JobCategory = jobCategory,
                Status = SessionStatus.GeneratingQuestions,
                CreatedAt = DateTime.UtcNow,
                TimeLimitSec = timeLimitSec,   // F2 — đóng dấu lựa chọn để câu THÍCH ỨNG sinh sau đọc lại
                // Phỏng vấn THÍCH ỨNG (B2C): đóng dấu toggle/trần từ cấu hình. Tắt → luồng batch tĩnh cũ.
                AdaptiveEnabled = _adaptive.Enabled,
                // F2b — adaptive BẬT: trần tổng số câu lấy theo lựa chọn của ứng viên (không chọn →
                // cấu hình). Adaptive TẮT: 0 = không trần (số câu do AIService sinh 1 lần, đã cap ở
                // questionCount rồi). CHECK ở DB chặn 0..20 cho mọi đường ghi.
                MaxQuestions = _adaptive.Enabled ? (questionCount ?? _adaptive.MaxQuestions) : 0,
                MaxFollowUps = _adaptive.Enabled ? _adaptive.MaxFollowUps : 0
            };
            _db.PracticeSessions.Add(session);
            await _db.SaveChangesAsync(ct);

            // Gọi Gemini NGOÀI transaction — không giữ DB connection lúc chờ AI.
            // Prompt tự xử 3 kịch bản: có JD ưu tiên JD; chỉ CV thì bám CV; không có
            // gì thì sinh câu hỏi chung theo JobCategory. focusCriteria (lesson /start) đưa thêm để bám tiêu chí.
            List<GeneratedQuestion> generated;
            try
            {
                // Dùng overload ĐẦY ĐỦ khi có focusCriteria (BC14) HOẶC ứng viên chọn số câu (F2b);
                // còn lại giữ nguyên overload 4 tham số của luồng thường (không đổi hợp đồng mock cũ).
                generated = focusCriteria is { Count: > 0 } || questionCount is not null
                    ? await _questionGenerator.GenerateQuestionsAsync(
                        session.JobCategory.ToString(), cvText, jdText, focusCriteria, questionCount, ct)
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
                await EnqueueGenerationFailedAbandonAsync(session, ct);   // BK12: outbox release credit (atomic Failed)
                // AI upstream lỗi (AiServiceException = transport/timeout/non-2xx) → propagate NGUYÊN
                // TYPE để controller map 502 (không bọc thành InvalidOperationException = 400, che lỗi
                // thật). Reserve vẫn được release ở catch ngoài (P1-2) + abandon (BK12) — idempotent PAY-11.
                // Lỗi khác (generic) giữ 400 như cũ.
                if (ex is AiServiceException) throw;
                throw new InvalidOperationException("Sinh câu hỏi thất bại", ex);
            }

            if (generated is null || generated.Count == 0)
            {
                session.Status = SessionStatus.Failed;
                await EnqueueGenerationFailedAbandonAsync(session, ct);   // BK12: outbox release credit (atomic Failed)
                throw new InvalidOperationException("AIService không trả về câu hỏi nào");
            }

            // Phỏng vấn THÍCH ỨNG (B2C): bật → giữ SeedCount câu đầu làm SEED (phần còn lại AI sinh động
            // theo câu trả lời trong AnswerService). Tắt → giữ CẢ bộ như luồng cũ. Kind=Seed (mặc định entity).
            var seedQuestions = _adaptive.Enabled
                ? generated.Take(Math.Max(1, _adaptive.SeedCount)).ToList()
                : generated;

            // Lưu câu hỏi + set Ready, commit #2 (tách commit tránh concurrency).
            var questions = seedQuestions
                .Select((q, idx) => new PracticeQuestion
                {
                    Id = Guid.NewGuid(),
                    SessionId = session.Id,
                    OrderNo = idx + 1,
                    Content = q.Content,
                    TimeLimitSec = session.TimeLimitSec,   // F2 — theo lựa chọn của ứng viên
                    Kind = QuestionKind.Seed
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
        catch (Exception ex)
        {
            // Bù trừ credit đã reserve: hoàn chỗ giữ để không treo credit ví User (PAY-5/PAY-11).
            // Best-effort — lỗi release chỉ log, KHÔNG che lỗi gốc (dùng CancellationToken.None để release
            // vẫn chạy kể cả khi lỗi gốc do ct bị hủy). Release idempotent nên an toàn khi nhánh gen-fail
            // (BK12) đã phát SessionAbandoned trước đó.
            try
            {
                await _reservationClient.ReleaseAsync(sessionId);
                _logger.LogInformation(
                    "P1-2: hoàn credit đã reserve cho session {SessionId} sau lỗi tạo session", sessionId);
            }
            catch (Exception releaseEx)
            {
                _logger.LogError(releaseEx,
                    "P1-2: hoàn credit thất bại cho session {SessionId} (lỗi gốc vẫn ném lại)", sessionId);
            }

            _logger.LogError(ex, "Tạo session {SessionId} thất bại sau khi reserve credit", sessionId);
            throw;
        }
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

        // BK14: reserve 1 credit ví ORG (owner=Org, PAY-6) TRƯỚC khi tạo session row — reserve-first
        // như B2C (BC2) để tránh orphan. sessionId cấp trước → reserve khoá idempotency theo đúng Id
        // session sẽ dùng (P4). Ví org hết credit → Payment 402 → InsufficientCreditException ném ở đây
        // ⇒ KHÔNG có row session (PAY-5). Consume/release sau đó do E7 xử theo owner của reservation.
        var sessionId = Guid.NewGuid();
        var reservation = await _reservationClient.ReserveAsync(
            ownerType: "Org", ownerId: request.OrgId, sessionId: sessionId, ct: ct);
        _logger.LogInformation(
            "BK14: reserve credit ví org {OrgId} cho session B2B {SessionId} (reservation {ReservationId})",
            request.OrgId, sessionId, reservation.ReservationId);

        // Reserve đã thành công (credit org đã giữ). Mọi lỗi sau đây → ReleaseAsync(sessionId) best-effort
        // (idempotent PAY-11) TRƯỚC khi ném lại, tránh treo credit org — đồng pattern B2C (P1-2).
        try
        {
            var session = new PracticeSession
            {
                Id = sessionId,
                CandidateId = candidateId,
                CampaignId = request.CampaignId,
                JobCategory = request.JobCategory,
                Status = SessionStatus.Ready,   // câu hỏi cấp sẵn → không cần sinh AI
                CreatedAt = DateTime.UtcNow,
                Deadline = request.ExpiresAt,   // I2: hạn chót nhận bài (B2B); null → không hard-deadline
                // Phỏng vấn THÍCH ỨNG (B2B): Campaign/HR bật → seed = TOÀN BỘ campaign questions (ai cũng
                // nhận cùng bộ, công bằng), câu thích ứng thêm ở đuôi (bounded), chấm theo cùng tiêu chí. null → tắt.
                AdaptiveEnabled = request.AdaptiveEnabled ?? false,
                MaxQuestions = ClampCampaignMaxQuestions(request.MaxQuestions, request.CampaignId),
                MaxFollowUps = request.MaxFollowUps ?? 0
            };
            _db.PracticeSessions.Add(session);

            var questions = request.Questions
                .Select((content, idx) => new PracticeQuestion
                {
                    Id = Guid.NewGuid(),
                    SessionId = session.Id,
                    OrderNo = idx + 1,
                    Content = content,
                    TimeLimitSec = DefaultTimeLimitSec,
                    Kind = QuestionKind.Seed
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
        catch (Exception ex)
        {
            try
            {
                await _reservationClient.ReleaseAsync(sessionId);
                _logger.LogInformation(
                    "BK14: hoàn credit org đã reserve cho session B2B {SessionId} sau lỗi tạo session", sessionId);
            }
            catch (Exception releaseEx)
            {
                _logger.LogError(releaseEx,
                    "BK14: hoàn credit org thất bại cho session B2B {SessionId} (lỗi gốc vẫn ném lại)", sessionId);
            }

            _logger.LogError(ex, "Tạo session B2B {SessionId} thất bại sau khi reserve credit org", sessionId);
            throw;
        }
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
            .Include(a => a.Scores).ThenInclude(sc => sc.Criterion)
            .AsSplitQuery()   // DB31: tránh 1 JOIN lặp transcript (TEXT) trên answers×scores×criteria
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

        if (!allDone)
        {
            _logger.LogInformation("Chốt session {SessionId} -> Scoring (đang chờ chấm nốt)", sessionId);
            return;
        }

        // PAY-13: nhánh "đóng-ngay" của submit (mọi answer đã terminal lúc submit) — nếu KHÔNG answer
        // nào Scored (mọi answer Failed/Skipped) → phát SessionAbandoned (release), không consume credit
        // cho buổi 0 answer chấm được (PAY-1). Đối xứng với AnswerService.TryCompleteSessionAsync.
        var scoredCount = statuses.Count(s => s == AnswerStatus.Scored);
        if (scoredCount == 0)
        {
            // DB2: đóng session (state) + ghi outbox-row abandoned CÙNG 1 SaveChanges (atomic).
            session.Status = SessionStatus.SessionAbandoned;
            await _scoringNotifier.EnqueueSessionAbandonedAsync(sessionId, "no_scored_answer", ct);
            await _db.SaveChangesAsync(ct);
            _logger.LogInformation(
                "Session {SessionId} -> SessionAbandoned ngay khi submit (không answer nào Scored)", sessionId);
            return;
        }

        // DB2: đóng session Scored (state) + ghi outbox-row SessionScored CÙNG 1 SaveChanges (atomic).
        session.Status = SessionStatus.Scored;
        await _scoringNotifier.EnqueueSessionScoredAsync(sessionId, ct);
        await _db.SaveChangesAsync(ct);
        _logger.LogInformation(
            "Session {SessionId} -> Scored ngay khi submit (đã chấm xong từ trước)", sessionId);

        // BC9/BC10/BC14/BC15: side-effect best-effort SAU khi đã commit (không chặn đóng session).
        await _scoringNotifier.NotifySessionScoredAsync(sessionId, ct);
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
            .Include(a => a.Scores).ThenInclude(sc => sc.Criterion)
            .AsSplitQuery()   // DB31: tránh 1 JOIN lặp transcript (TEXT) trên answers×scores×criteria
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
    // DB31 — keyset-paged (mẫu DB8, dùng chung Isas.Shared/Pagination). Trước đây KHÔNG có
    // Skip/Take/cursor → trả TOÀN BỘ lịch sử phỏng vấn trọn đời của candidate trong 1 payload.
    // Backward-compat y hệt DB8: body vẫn là mảng JSON, cursor opaque + limit là opt-in
    // (`?cursor=&limit=`), next-cursor ở header X-Next-Cursor, limit mặc định = trần cũ.
    public async Task<KeysetPage<PracticeSessionSummary>> GetHistoryAsync(
        Guid candidateId, string? cursor = null, int? limit = null, CancellationToken ct = default)
    {
        var take = KeysetPaging.ClampLimit(limit);
        var cur = KeysetCursor.Decode(cursor);

        var query = _db.PracticeSessions
            .AsNoTracking()
            .Where(s => s.CandidateId == candidateId);

        if (cur is not null)
            query = query.Where(s => s.CreatedAt < cur.CreatedAt
                || (s.CreatedAt == cur.CreatedAt && s.Id.CompareTo(cur.Id) < 0));

        var rows = await query
            .OrderByDescending(s => s.CreatedAt)
            .ThenByDescending(s => s.Id)
            .Take(take)
            .Select(s => new PracticeSessionSummary(
                s.Id, s.Status.ToString(), s.JobCategory.ToString(),
                s.CreatedAt, s.CompletedAt, s.OverallScore))   // BC9: lịch sử hiện điểm tổng
            .ToListAsync(ct);

        var next = rows.Count == take
            ? new KeysetCursor(rows[^1].CreatedAt, rows[^1].Id).Encode()
            : null;
        return new KeysetPage<PracticeSessionSummary>(rows, next);
    }

    // DB18 — Payment (internal) dò orphan reservation: trả TẬP CON sessionIds có row practice_sessions
    // (bất kể status). Reservation Reserved mà session KHÔNG tồn tại (crash giữa reserve↔insert lúc Start)
    // = orphan → Payment release. Distinct để không phụ thuộc caller; rỗng → rỗng (không query).
    public async Task<IReadOnlyList<Guid>> GetExistingSessionIdsAsync(
        IReadOnlyList<Guid> sessionIds, CancellationToken ct = default)
    {
        if (sessionIds is null || sessionIds.Count == 0)
            return Array.Empty<Guid>();

        var ids = sessionIds.Distinct().ToList();
        return await _db.PracticeSessions
            .AsNoTracking()
            .Where(s => ids.Contains(s.Id))
            .Select(s => s.Id)
            .ToListAsync(ct);
    }

    // AI4 — INTERNAL (Campaign/HR): trả per-question list kèm transcript + nhận xét AI per-criterion +
    // cờ needs_review (E10/E11). Tái dùng NGUYÊN VẸN truy vấn + MapAnswer của GetSessionAsync (một nguồn
    // sự thật cho transcript/điểm) NHƯNG BỎ check chủ session — caller là máy-máy (X-Internal-Token) và
    // Campaign đã gate org+ranking. MapToResponse với criterionScores/cvStrengths mặc định null → phần
    // Result (BC9/BC8) = null; ta chỉ lấy .Questions. Session không tồn tại → null (controller 404).
    public async Task<IReadOnlyList<QuestionResponse>?> GetSessionAnswersInternalAsync(
        Guid sessionId, CancellationToken ct = default)
    {
        var session = await _db.PracticeSessions
            .AsNoTracking()
            .FirstOrDefaultAsync(s => s.Id == sessionId, ct);

        if (session is null) return null;

        var questions = await _db.PracticeQuestions
            .AsNoTracking()
            .Where(q => q.SessionId == sessionId)
            .OrderBy(q => q.OrderNo)
            .ToListAsync(ct);

        var answers = await _db.PracticeAnswers
            .AsNoTracking()
            .Include(a => a.Scores).ThenInclude(sc => sc.Criterion)
            .AsSplitQuery()   // DB31: tránh 1 JOIN lặp transcript (TEXT) trên answers×scores×criteria
            .Where(a => a.SessionId == sessionId)
            .ToListAsync(ct);

        return MapToResponse(session, questions, answers).Questions;
    }

    // ── helpers ───────────────────────────────────────────────────────────

    // BK12: B2C reserve credit ví cá nhân (BC2) TRƯỚC khi sinh câu hỏi. Nếu AI sinh câu hỏi lỗi →
    // session `Failed`, credit đang bị KẸT: E3 sweeper chỉ quét `InProgress`, còn `Failed` KHÔNG tự
    // phát `SessionAbandoned` → E7 (Payment) không release → orphan credit. Fix: ghi outbox-row
    // `SessionAbandoned(reason=generation_failed)` để OutboxDispatcher phát → E7 hoàn credit ví User.
    // DB2: ghi outbox-row CÙNG SaveChanges với state=Failed (atomic — broker chết vẫn còn row để gửi lại).
    // SettlementReconciler cũ BỎ SÓT site này (chỉ quét Scored/SessionAbandoned); outbox phủ cả nó. Chỉ
    // B2C dùng path này (CreateSessionAsync); B2B không reserve (PAY-6) và không có nhánh Failed-sau-reserve.
    private async Task EnqueueGenerationFailedAbandonAsync(PracticeSession session, CancellationToken ct)
    {
        await _scoringNotifier.EnqueueSessionAbandonedAsync(session.Id, GenerationFailedReason, ct);
        await _db.SaveChangesAsync(ct);   // atomic: state=Failed + outbox-row
        _logger.LogInformation(
            "BK12: ghi outbox SessionAbandoned(generation_failed) cho session {SessionId} để release credit ví User",
            session.Id);

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

    // Chuẩn hoá text nhập tay: rỗng/toàn khoảng trắng = KHÔNG nhập (null), còn lại thì trim.
    // Giống hệt CampaignService.NormalizeText (C11) → "gửi jdText rỗng" hành xử như không gửi ở cả 2 dòng.
    // + cap độ dài (TextInputLimits.JdTextMaxChars — ngưỡng CHUNG với B2B/Campaign): JD nhập tay đi thẳng
    // vào prompt Gemini → vượt ngưỡng ném InvalidOperationException (controller map → 400) kèm giới hạn và
    // độ dài đang gửi. Đo SAU khi trim → khoảng trắng thừa không tính vào ngưỡng.
    private static string? NormalizeText(string? text)
        => TextInputLimits.NormalizeAndEnsureLimit(
            text, JdTextLabel, msg => new InvalidOperationException(msg));

    // Nhãn field trong thông báo lỗi 400 — khớp tên field client gửi lên.
    private const string JdTextLabel = "Mô tả công việc (jdText)";

    // F2 — thời lượng mỗi câu ứng viên được chọn. Tập ĐÓNG (không phải khoảng): 3 mốc để UI là nhóm nút
    // chọn, và để mọi buổi so sánh được với nhau. Tập nằm ở tầng service chứ KHÔNG đưa vào CHECK của DB —
    // đổi lựa chọn sau này (thêm 180s chẳng hạn) sẽ phải chạy migration chỉ để sửa một danh sách UI.
    private static readonly int[] AllowedTimeLimitsSec = [60, 120, 240];

    // null = client cũ không gửi → giữ mặc định 120 (hành vi trước F2, không phải lỗi).
    // ⚠ Ném InvalidOperationException chứ KHÔNG phải ArgumentException: PracticeController chỉ bắt
    // InvalidOperationException → 400; ArgumentException rơi xuống catch(Exception) → 500. Cùng kiểu với
    // guard jobCategory ngay đầu CreateSessionInternalAsync.
    private static int ValidateTimeLimitSec(int? requested)
    {
        if (requested is null) return DefaultTimeLimitSec;
        if (!AllowedTimeLimitsSec.Contains(requested.Value))
            throw new InvalidOperationException(
                $"timeLimitSec chỉ nhận {string.Join(" / ", AllowedTimeLimitsSec)} giây (đang gửi: {requested.Value}).");
        return requested.Value;
    }

    // F2b — trần số câu.
    //
    // VÌ SAO PHẢI CÓ TRẦN: chi phí tăng TUYẾN TÍNH theo số câu (mỗi câu = 1 lượt Whisper + N lần gọi
    // Gemini do self-consistency + 1 lần TTS gần như luôn miss cache) nhưng doanh thu là HẰNG SỐ 1
    // credit/buổi — ReserveAsync gọi đúng một lần lúc tạo session, không scale theo số câu. Không có
    // trần thì một người chọn 500 câu vừa ăn hết biên credit-to-cost vừa làm nghẽn queue chấm của
    // mọi người khác (Whisper chạy CPU, xử lý tuần tự).
    private const int MinQuestionCount = 1;
    private const int MaxQuestionCount = 20;

    // null = client không chọn → trả null để KHÔNG ghi đè mặc định của AIService (giữ hành vi cũ = 5 câu).
    private static int? ValidateQuestionCount(int? requested)
    {
        if (requested is null) return null;
        if (requested.Value is < MinQuestionCount or > MaxQuestionCount)
            throw new InvalidOperationException(
                $"questionCount phải nằm trong khoảng {MinQuestionCount}..{MaxQuestionCount} (đang gửi: {requested.Value}).");
        return requested.Value;
    }

    /// <summary>
    /// F2b — kẹp trần câu thích ứng của B2B về đúng miền CHECK ở DB (0..20).
    ///
    /// VÌ SAO KẸP CHỨ KHÔNG NÉM: `CampaignService.ValidateAdaptiveCaps` hiện chỉ chặn số ÂM, nên HR
    /// đặt `max_questions = 100000` là qua sạch guard phía Campaign. Nếu ở đây để nguyên giá trị đó
    /// thì CHECK `ck_practice_sessions_max_questions_range` sẽ nổ ngay lúc INSERT — tức là ứng viên
    /// bấm "Bắt đầu" và nhận lỗi, SAU KHI credit org đã bị reserve. Đổi một cấu hình sai của HR lấy
    /// một buổi thi hỏng là đánh đổi tệ; kẹp + log để HR sửa cấu hình mà ứng viên vẫn thi được.
    ///
    /// Chỗ sửa ĐÚNG là siết `ValidateAdaptiveCaps` phía Campaign (ngoài phạm vi worker này — file đó
    /// đang do người khác giữ trong vòng này). Đây là lưới an toàn, không phải bản vá thay thế.
    /// </summary>
    private int ClampCampaignMaxQuestions(int? requested, Guid campaignId)
    {
        var value = requested ?? 0;
        if (value >= 0 && value <= MaxQuestionCount) return value;

        var clamped = Math.Clamp(value, 0, MaxQuestionCount);
        _logger.LogWarning(
            "Campaign {CampaignId} cấu hình max_questions={Requested} ngoài miền 0..{Max} → kẹp về {Clamped}",
            campaignId, value, MaxQuestionCount, clamped);
        return clamped;
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
                answerByQuestion.TryGetValue(q.Id, out var a) ? MapAnswer(a) : null,
                q.Kind.ToString()))   // phỏng vấn THÍCH ỨNG — Seed | FollowUp | Clarify | NewQuestion
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
        // E10 — mỗi tiêu chí: điểm chốt = MEDIAN qua các attempt (self-consistency); reasoning/level
        // lấy từ attempt ĐẠI DIỆN (điểm gần median nhất, tie-break attempt mới nhất) để nhận xét khớp
        // điểm hiển thị. N=1 → median = giá trị attempt đó, đại diện = chính nó → giữ hiển thị cũ.
        var perCriterion = a.Scores
            .GroupBy(sc => sc.CriterionId)
            .Select(g =>
            {
                var median = ScoreStatistics.Median(g.Select(s => s.Score));
                var rep = g.OrderBy(s => Math.Abs(s.Score - median))
                           .ThenByDescending(s => s.AttemptNo)
                           .First();
                // Criterion nạp qua .ThenInclude ở các site đọc; dùng `?.` để site nào lỡ quên Include
                // thì ra null (client lùi về nhãn chung) thay vì ném NRE giữa luồng xem kết quả.
                return new AnswerScoreResponse(
                    g.Key, median, rep.Reasoning, rep.RubricVersion, rep.LevelMatched,
                    rep.Criterion?.Name, rep.Criterion?.MaxScore);
            })
            .ToList();

        return new AnswerResponse(
            a.Id, a.Status.ToString(), a.DurationSec, a.Transcript, perCriterion, a.NeedsReview,
            a.SampleAnswer);   // F13 — gợi ý câu trả lời mẫu (null khi chưa chấm / LLM không trả)
    }
}