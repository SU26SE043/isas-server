using Isas.InterviewService.ApplicationDbContext;
using Isas.InterviewService.DTOs;
using Isas.InterviewService.Entities;
using Isas.InterviewService.Enums;
using Isas.InterviewService.Models;
using Isas.InterviewService.Services.Interfaces;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Options;

namespace Isas.InterviewService.Services;

public class AnswerService : IAnswerService
{
    // Phỏng vấn THÍCH ỨNG — thời lượng cho câu hỏi thích ứng (giống seed B2C: PracticeService.DefaultTimeLimitSec).
    private const int AdaptiveQuestionTimeLimitSec = 120;

    private readonly InterviewDbContext _db;
    private readonly IStorageService _storage;
    private readonly IScoringJobPublisher _scoringPublisher;
    private readonly ISessionScoringNotifier _scoringNotifier;
    private readonly ScoringOptions _scoring;   // E10 — self-consistency (N, ngưỡng spread, temp)
    private readonly IAiServiceInterviewDecider? _decider;   // phỏng vấn THÍCH ỨNG (null = tắt / test cũ)
    private readonly ILogger<AnswerService> _logger;

    public AnswerService(
        InterviewDbContext db,
        IStorageService storage,
        IScoringJobPublisher scoringPublisher,
        ISessionScoringNotifier scoringNotifier,
        IOptions<ScoringOptions> scoringOptions,
        ILogger<AnswerService> logger,
        // Optional (default null) → mọi test dựng AnswerService cũ (6 tham số) vẫn compile + adaptive tắt;
        // DI inject bản thật khi đăng ký (AddHttpClient). Adaptive chỉ chạy khi decider != null VÀ session bật.
        IAiServiceInterviewDecider? decider = null)
    {
        _db = db;
        _storage = storage;
        _scoringPublisher = scoringPublisher;
        _scoringNotifier = scoringNotifier;
        _scoring = scoringOptions.Value;
        _decider = decider;
        _logger = logger;
    }

    public async Task<UploadAnswerResult> UploadAnswerAsync(
        Guid sessionId,
        Guid questionId,
        Guid candidateId,
        Stream audioStream,
        string contentType,
        int durationSec,
        CancellationToken ct = default)
    {
        // 1. Session tồn tại + đúng chủ
        var session = await _db.PracticeSessions
            .FirstOrDefaultAsync(s => s.Id == sessionId, ct)
            ?? throw new KeyNotFoundException("Session không tồn tại");

        if (session.CandidateId != candidateId)
            throw new UnauthorizedAccessException("Không phải buổi của bạn");

        // 2. Buổi đã kết thúc thì không cho upload thêm (SessionAbandoned — E3 — cũng là trạng
        // thái chốt/terminal, không nhận thêm answer).
        if (session.Status is SessionStatus.Completed
            or SessionStatus.Scoring or SessionStatus.Scored or SessionStatus.SessionAbandoned)
            throw new InvalidOperationException("Buổi đã kết thúc");

        // 3. Câu hỏi thuộc đúng buổi
        var question = await _db.PracticeQuestions
            .FirstOrDefaultAsync(q => q.Id == questionId && q.SessionId == sessionId, ct)
            ?? throw new KeyNotFoundException("Câu hỏi không thuộc buổi này");

        // 4. Tìm answer cũ (retry) - business rule: tối đa 1 answer mỗi câu.
        //    Include(Scores) để re-upload dọn sạch điểm cũ (INT-3) — xem nhánh else bên dưới.
        var answer = await _db.PracticeAnswers
            .Include(a => a.Scores)
            .FirstOrDefaultAsync(a => a.SessionId == sessionId && a.QuestionId == questionId, ct);

        // 5. fileId = answerId -> retry ghi đè đúng object (idempotent)
        var answerId = answer?.Id ?? Guid.NewGuid();

        // 6. Upload audio qua StorageService chung
        var storagePath = await _storage.UploadAsync(
            fileStream: audioStream,
            fileType: "answer-audio",
            userId: candidateId,
            fileId: answerId,
            ext: "webm",
            contentType: contentType,
            ct: ct);

        // 7. Upsert PracticeAnswer
        if (answer is null)
        {
            answer = new PracticeAnswer
            {
                Id = answerId,
                SessionId = sessionId,
                QuestionId = questionId,
                AudioObjectKey = storagePath,
                DurationSec = durationSec,
                Status = AnswerStatus.Uploaded
            };
            _db.PracticeAnswers.Add(answer);
        }
        else
        {
            // INT-3 — upload lại = ghi đè: reset audio/transcript/status VÀ dọn sạch điểm cũ. Nếu giữ
            // lại answer.Scores thì (a) GET trả điểm của bài CŨ tới khi có callback mới, (b) khi candidate
            // đổi rubric riêng (BC16 → rubric_version mới) median E10 sẽ trộn điểm lệch version. Xoá điểm
            // + reset needs_review → chấm lại từ đầu sạch (callback mới ghi điểm attempt mới).
            answer.AudioObjectKey = storagePath;
            answer.DurationSec = durationSec;
            answer.Status = AnswerStatus.Uploaded;
            answer.Transcript = null;
            if (answer.Scores.Count > 0)
                _db.AnswerScores.RemoveRange(answer.Scores);
            answer.NeedsReview = false;
        }

        // Câu trả lời đầu tiên -> session Ready chuyển InProgress.
        if (session.Status == SessionStatus.Ready)
            session.Status = SessionStatus.InProgress;

        await _db.SaveChangesAsync(ct);

        _logger.LogInformation(
            "Saved answer {AnswerId} for question {QuestionId} in session {SessionId}",
            answer.Id, questionId, sessionId);

        // Phỏng vấn THÍCH ỨNG — answer đã durable (SaveChanges trên) TRƯỚC mọi call AIService → decide lỗi
        // KHÔNG làm mất câu trả lời. Chỉ chạy khi bật (decider != null + session.AdaptiveEnabled). Trả về
        // transcript đồng bộ (đã lưu lên answer) + câu hỏi kế (nếu có) để (a) đẩy transcript vào ScoringJob
        // → worker bỏ Whisper, (b) client hiện câu kế ngay trong response (không cần poll GET).
        var adaptive = await TryRunAdaptiveAsync(session, question, answer, ct);

        // 8. Chấm dần: publish job ngay sau khi lưu (kèm transcript đồng bộ nếu adaptive đã transcribe).
        //    Publish lỗi KHÔNG làm hỏng upload — answer đã lưu, để re-publish sau.
        await TryPublishScoringJobAsync(session, question, answer, ct);

        return new UploadAnswerResult(
            answer.Id, questionId, answer.Status.ToString(),
            Transcript: answer.Transcript,
            NextAction: adaptive.Action,
            NextQuestion: adaptive.AppendedQuestion is null ? null : new NextQuestionResponse(
                adaptive.AppendedQuestion.Id, adaptive.AppendedQuestion.OrderNo,
                adaptive.AppendedQuestion.Content, adaptive.AppendedQuestion.TimeLimitSec,
                adaptive.AppendedQuestion.Kind.ToString()),
            InterviewComplete: adaptive.InterviewComplete);
    }

    // ── Phỏng vấn THÍCH ỨNG: transcribe đồng bộ + quyết định + append câu kế ──────────────
    private sealed record AdaptiveOutcome(
        string? Action, PracticeQuestion? AppendedQuestion, bool InterviewComplete)
    {
        // Không chạy adaptive (tắt / chưa tới frontier / decide lỗi) → không có tín hiệu, không "complete".
        public static readonly AdaptiveOutcome None = new(null, null, false);
    }

    // Chạy vòng thích ứng SAU khi answer đã lưu durable. Bọc toàn bộ trong try/catch → mọi lỗi (kể cả
    // đua unique index khi double-POST) chỉ log + trả None: upload LUÔN thành công, degrade về luồng tĩnh.
    private async Task<AdaptiveOutcome> TryRunAdaptiveAsync(
        PracticeSession session, PracticeQuestion question, PracticeAnswer answer, CancellationToken ct)
    {
        if (_decider is null || !session.AdaptiveEnabled)
            return AdaptiveOutcome.None;

        try
        {
            // (1) Chỉ append khi MỌI câu hiện tại của buổi đã có answer (frontier tuyến tính, đúng cho cả
            // B2C 1-seed lẫn B2B seeds-first — độc lập thứ tự trả lời). Còn câu chưa trả lời (kể cả child
            // đã sinh trước đó) → chưa append (⇒ re-upload câu cũ / re-upload frontier sau khi đã có child
            // đều không sinh trùng). answer vừa được SaveChanges ở trên nên đã tính vào truy vấn này.
            var pendingCount = await _db.PracticeQuestions
                .CountAsync(q => q.SessionId == session.Id
                                 && !_db.PracticeAnswers.Any(a => a.QuestionId == q.Id), ct);
            if (pendingCount > 0)
                return AdaptiveOutcome.None;

            // (2) Idempotency: answer này đã "đẻ" câu kế rồi (re-upload) → không sinh trùng (unique index backstop).
            var alreadyHasChild = await _db.PracticeQuestions
                .AnyAsync(q => q.GeneratedFromAnswerId == answer.Id, ct);
            if (alreadyHasChild)
                return AdaptiveOutcome.None;

            // (3) Ngân sách — hết trần câu / trần thích ứng → kết thúc, KHÔNG gọi AI (tiết kiệm latency/cost).
            var askedCount = await _db.PracticeQuestions.CountAsync(q => q.SessionId == session.Id, ct);
            var followUpCount = await _db.PracticeQuestions
                .CountAsync(q => q.SessionId == session.Id && q.Kind != QuestionKind.Seed, ct);
            var budgetLeft = (session.MaxQuestions <= 0 || askedCount < session.MaxQuestions)
                             && (session.MaxFollowUps <= 0 || followUpCount < session.MaxFollowUps);
            if (!budgetLeft)
                return new AdaptiveOutcome("end", null, InterviewComplete: true);

            // (4) Quá hạn nhận bài (B2B) → không hỏi thêm (đối xứng SessionAbandonSweeper).
            if (session.Deadline is DateTime dl && DateTime.UtcNow > dl)
                return new AdaptiveOutcome("end", null, InterviewComplete: true);

            // (5) Lịch sử Q&A + tiêu chí (CÙNG nguồn scoring → follow-up bám cùng rubric, công bằng B2B).
            var history = await BuildAdaptiveHistoryAsync(session.Id, question.Id, ct);
            var criteria = (await LoadActiveCriteriaAsync(session, ct))
                .Select(c => new DecideCriterionDto(c.Name, c.Description))
                .ToList();

            // (6) Transcribe đồng bộ + quyết định. Lỗi → nuốt ở catch ngoài → None (degrade tĩnh).
            var decision = await _decider!.DecideNextAsync(
                answer.AudioObjectKey!, session.JobCategory.ToString(), question.Content,
                history, askedCount, followUpCount, session.MaxQuestions, session.MaxFollowUps, criteria, ct);

            // (7) Lưu transcript đồng bộ lên answer (single-source; TryPublishScoringJobAsync đọc lại → job).
            if (!string.IsNullOrWhiteSpace(decision.Transcript))
            {
                answer.Transcript = decision.Transcript;
                await _db.SaveChangesAsync(ct);
            }

            // (8) end / không có câu hỏi → mời submit (không append).
            if (decision.Action == "end" || string.IsNullOrWhiteSpace(decision.NextQuestion))
                return new AdaptiveOutcome(decision.Action, null, InterviewComplete: true);

            // (9) Append 1 câu kế ở đuôi (OrderNo = max + 1), gắn GeneratedFromAnswerId (idempotency).
            var maxOrder = await _db.PracticeQuestions
                .Where(q => q.SessionId == session.Id)
                .MaxAsync(q => q.OrderNo, ct);
            var newQuestion = new PracticeQuestion
            {
                Id = Guid.NewGuid(),
                SessionId = session.Id,
                OrderNo = maxOrder + 1,
                Content = decision.NextQuestion!,
                TimeLimitSec = AdaptiveQuestionTimeLimitSec,
                Kind = MapKind(decision.Action),
                GeneratedFromAnswerId = answer.Id
            };
            _db.PracticeQuestions.Add(newQuestion);

            try
            {
                await _db.SaveChangesAsync(ct);
            }
            catch (DbUpdateException ex)
            {
                // Đua double-POST: unique index generated_from_answer_id chặn child thứ 2. Gỡ entity khỏi
                // tracker để lần SaveChanges kế (TryPublishScoringJobAsync) không cố lưu lại row hỏng.
                _db.Entry(newQuestion).State = EntityState.Detached;
                _logger.LogWarning(ex,
                    "Adaptive: append câu kế bị chặn (đua unique) cho answer {AnswerId} — bỏ qua", answer.Id);
                return AdaptiveOutcome.None;
            }

            _logger.LogInformation(
                "Adaptive: session {SessionId} answer {AnswerId} → {Action}, thêm câu {QuestionId} (order {Order})",
                session.Id, answer.Id, decision.Action, newQuestion.Id, newQuestion.OrderNo);

            return new AdaptiveOutcome(decision.Action, newQuestion, InterviewComplete: false);
        }
        catch (Exception ex)
        {
            // Degrade về luồng tĩnh: answer đã lưu, worker sẽ transcribe async như cũ. Upload KHÔNG hỏng.
            _logger.LogWarning(ex,
                "Adaptive decide-next lỗi cho answer {AnswerId} — bỏ qua (fallback luồng tĩnh)", answer.Id);
            return AdaptiveOutcome.None;
        }
    }

    private static QuestionKind MapKind(string action) => action switch
    {
        "follow_up" => QuestionKind.FollowUp,
        "clarify" => QuestionKind.Clarify,
        "new_question" => QuestionKind.NewQuestion,
        _ => QuestionKind.NewQuestion   // phòng hờ (end đã return trước, không tới đây)
    };

    // Lịch sử Q&A TRƯỚC câu hiện tại (currentQuestion + transcript mới nhất gửi riêng): câu theo OrderNo
    // + transcript answer tương ứng. BỎ câu hiện tại (đang hỏi câu kế của nó). Lưu ý: B2C (1 seed) mỗi
    // lượt trước đã transcribe đồng bộ → transcript đầy đủ; B2B seeds-first, các seed trước KHÔNG phải
    // frontier nên transcript có thể còn null (worker chấm async chưa xong) → prompt hiển thị "(trống)",
    // quyết định bám chủ yếu câu trả lời mới nhất (chấp nhận được — câu thích ứng B2B là phần bonus ở đuôi).
    private async Task<List<DecideTurnDto>> BuildAdaptiveHistoryAsync(
        Guid sessionId, Guid currentQuestionId, CancellationToken ct)
    {
        var questions = await _db.PracticeQuestions.AsNoTracking()
            .Where(q => q.SessionId == sessionId && q.Id != currentQuestionId)
            .OrderBy(q => q.OrderNo)
            .Select(q => new { q.Id, q.Content, q.Kind })
            .ToListAsync(ct);

        var transcripts = await _db.PracticeAnswers.AsNoTracking()
            .Where(a => a.SessionId == sessionId)
            .Select(a => new { a.QuestionId, a.Transcript })
            .ToListAsync(ct);
        var byQuestion = transcripts.ToDictionary(x => x.QuestionId, x => x.Transcript);

        return questions
            .Select(q => new DecideTurnDto(
                q.Content, byQuestion.GetValueOrDefault(q.Id), q.Kind.ToString()))
            .ToList();
    }

    private async Task TryPublishScoringJobAsync(
        PracticeSession session, PracticeQuestion question, PracticeAnswer answer,
        CancellationToken ct)
    {
        try
        {
            // Nguồn tiêu chí tùy mode (E1): B2B chấm theo tiêu chí campaign, B2C theo rubric nghề (kèm
            // rubric_levels cho mức neo E9). Dùng chung LoadActiveCriteriaAsync với vòng adaptive để 2 đường
            // (publish chấm + quyết định câu kế) luôn khớp nguồn tiêu chí.
            var criteria = await LoadActiveCriteriaAsync(session, ct);

            if (criteria.Count == 0)
            {
                _logger.LogWarning(
                    "Không có tiêu chí active (campaign={CampaignId}, nghề={JobCategory}) — bỏ qua publish answer {AnswerId}",
                    session.CampaignId, session.JobCategory, answer.Id);
                return;
            }

            // Tất cả criterion active của 1 nghề dùng chung 1 version.
            var rubricVersion = criteria[0].Version;
            var builtCriteria = ScoringCriteriaBuilder.Build(criteria);   // E9: kèm levels (+ anchors)

            // E10 — self-consistency: publish N job (attempt 1..N) cho cùng 1 answer để chấm N lần.
            //   attempt 1 → temp=0 (tái lập); 2..N → SelfConsistencyTemperature (dao động thật để đo spread).
            //   N=1 (mặc định) → đúng 1 job như cũ. Worker echo attempt_no về callback → .NET lưu theo attempt.
            var n = Math.Max(1, _scoring.SelfConsistencyN);
            for (int attempt = 1; attempt <= n; attempt++)
            {
                var job = new ScoringJob
                {
                    AnswerId = answer.Id,
                    SessionId = session.Id,
                    QuestionId = question.Id,
                    AudioObjectKey = answer.AudioObjectKey!,
                    QuestionContent = question.Content,
                    JobCategory = session.JobCategory.ToString(),
                    RubricVersion = rubricVersion,
                    Criteria = builtCriteria,
                    AttemptNo = attempt,
                    Temperature = attempt == 1 ? 0d : _scoring.SelfConsistencyTemperature,
                    // Phỏng vấn THÍCH ỨNG — transcript đã transcribe đồng bộ (adaptive) → worker bỏ Whisper.
                    // null (luồng tĩnh / adaptive tắt / decide lỗi) → worker tải audio + Whisper như cũ.
                    Transcript = answer.Transcript
                };

                await _scoringPublisher.PublishAsync(job, ct);
            }

            // Publish OK -> Uploaded chuyển Scoring + ghi mốc publish.
            // Republisher dựa vào đây để KHÔNG nhặt nhầm answer đang chờ worker
            // chấm (queue serial + Whisper CPU có thể lâu).
            answer.Status = AnswerStatus.Scoring;
            answer.LastScoringPublishedAt = DateTime.UtcNow;
            await _db.SaveChangesAsync(ct);
        }
        catch (Exception ex)
        {
            // Nuốt lỗi publish: upload vẫn thành công với người dùng.
            // Answer giữ nguyên Uploaded + LastScoringPublishedAt=null -> republisher
            // sẽ đẩy lại sớm (publish hụt).
            _logger.LogError(ex,
                "Publish job chấm thất bại cho answer {AnswerId}. Answer vẫn ở Uploaded, sẽ re-publish sau.",
                answer.Id);
        }
    }

    // Nguồn tiêu chí active theo mode (E1/BC16) — dùng chung cho publish chấm + quyết định câu kế (adaptive):
    //   B2B: theo campaign_id. B2C: rubric RIÊNG của candidate cho nghề (nếu có) else seed mặc định (owner null).
    // Criteria materialize của campaign cũng mang JobCategory → B2C lọc thêm campaign_id IS NULL (không chấm
    // nhầm bằng tiêu chí campaign cùng nghề). E9: .Include(Levels) để có mức neo (câu mẫu jsonb trên level, DB15).
    private async Task<List<RubricCriterion>> LoadActiveCriteriaAsync(
        PracticeSession session, CancellationToken ct)
    {
        var query = _db.RubricCriteria.AsNoTracking()
            .Include(c => c.Levels)
            .Where(c => c.IsActive);
        if (session.CampaignId is Guid campaignId)
        {
            query = query.Where(c => c.CampaignId == campaignId);
        }
        else
        {
            var owner = await B2CRubricScope.ResolveOwnerAsync(_db, session.CandidateId, session.JobCategory, ct);
            query = owner is Guid oid
                ? query.Where(c => c.CampaignId == null && c.CandidateId == oid && c.JobCategory == session.JobCategory)
                : query.Where(c => c.CampaignId == null && c.CandidateId == null && c.JobCategory == session.JobCategory);
        }
        return await query.ToListAsync(ct);
    }
    // ── Callback: lưu transcript + điểm từ worker Python ──────────────────
    public async Task SaveResultAsync(
        Guid answerId, AnswerScoreCallbackRequest req, CancellationToken ct = default)
    {
        var answer = await _db.PracticeAnswers
            .Include(a => a.Scores)
            .FirstOrDefaultAsync(a => a.Id == answerId, ct)
            ?? throw new KeyNotFoundException($"Answer {answerId} không tồn tại");

        // E8 — Guard điểm phía C# (defense-in-depth). Worker Python đã kẹp/lọc, NHƯNG AIService
        // deploy ephemeral (docker cp) nên image có thể lệch → không tin worker 100%.
        // Nạp bộ tiêu chí thuộc rubric của session (đúng nguồn đã dùng lúc publish job — E1):
        //   B2B chấm theo tiêu chí campaign; B2C theo rubric nghề (campaign_id IS NULL).
        // Dùng bản đồ criterionId -> maxScore để (a) BỎ criterion ngoài rubric, (b) KẸP [0, maxScore].
        var session = await _db.PracticeSessions.AsNoTracking()
            .FirstOrDefaultAsync(s => s.Id == answer.SessionId, ct);
        var critQuery = _db.RubricCriteria.AsNoTracking().Include(c => c.Levels).Where(c => c.IsActive);
        if (session?.CampaignId is Guid campaignId)
        {
            critQuery = critQuery.Where(c => c.CampaignId == campaignId);
        }
        else
        {
            // BC16: khớp CHÍNH XÁC nguồn đã dùng lúc publish (E1) — B2C ưu tiên rubric RIÊNG của candidate.
            var owner = await B2CRubricScope.ResolveOwnerAsync(_db, session!.CandidateId, session.JobCategory, ct);
            critQuery = owner is Guid oid
                ? critQuery.Where(c => c.CampaignId == null && c.CandidateId == oid && c.JobCategory == session.JobCategory)
                : critQuery.Where(c => c.CampaignId == null && c.CandidateId == null && c.JobCategory == session.JobCategory);
        }
        // E8/E9: bản đồ criterionId -> tiêu chí (kèm rubric_levels) để BỎ criterion ngoài rubric,
        // KẸP [0,maxScore], và (E9) snap/lưu level_matched theo mức của tiêu chí.
        var critById = (await critQuery.ToListAsync(ct)).ToDictionary(c => c.Id);

        // E10 — attempt worker vừa chấm (echo từ job). Worker cũ không gửi → DTO default 1.
        var attemptNo = req.AttemptNo <= 0 ? 1 : req.AttemptNo;

        // Idempotency: worker retry có thể gửi lại cùng attempt+version.
        // Xoá điểm cũ cùng attempt+version rồi ghi lại, tránh nhân đôi.
        var stale = answer.Scores
            .Where(s => s.AttemptNo == attemptNo && s.RubricVersion == req.RubricVersion)
            .ToList();
        if (stale.Count > 0)
            _db.AnswerScores.RemoveRange(stale);

        answer.Transcript = req.Transcript;

        foreach (var item in req.Scores)
        {
            // E8: criterion không thuộc rubric của session (AI bịa / image lệch) → BỎ (không lưu).
            if (!critById.TryGetValue(item.CriterionId, out var crit))
            {
                _logger.LogWarning(
                    "Bỏ điểm criterion {CriterionId} không thuộc rubric session {SessionId} (answer {AnswerId})",
                    item.CriterionId, answer.SessionId, answerId);
                continue;
            }

            var maxScore = crit.MaxScore;

            // E8: kẹp điểm về [0, maxScore] của tiêu chí (INT-9) — chống worker/image trả điểm lệch trần.
            var clamped = Math.Clamp(item.Score, 0m, maxScore);
            if (clamped != item.Score)
                _logger.LogWarning(
                    "Kẹp điểm criterion {CriterionId} answer {AnswerId}: {Raw} -> {Clamped} (maxScore={MaxScore})",
                    item.CriterionId, answerId, item.Score, clamped, maxScore);

            // E9: neo điểm theo mức của tiêu chí.
            //  - Tiêu chí CÓ rubric_levels khai: HARD anchor → snap điểm về mức gần nhất (KHÔNG drop,
            //    tránh thiếu-tiêu-chí → Failed INT-9); score = level.score. Ưu tiên levelMatched worker
            //    gửi nếu hợp lệ, ngược lại snap theo điểm đã kẹp.
            //  - Tiêu chí dùng dải mặc định (không khai levels): giữ điểm (kẹp) như E8; chỉ LƯU
            //    levelMatched nếu worker gửi giá trị nằm trong [0,maxScore] (tương thích worker cũ).
            var (finalScore, levelMatched) = ResolveLevel(crit, clamped, item.LevelMatched, answerId);

            _db.AnswerScores.Add(new AnswerScore
            {
                Id = Guid.NewGuid(),
                AnswerId = answer.Id,
                CriterionId = item.CriterionId,
                Score = finalScore,
                LevelMatched = levelMatched,
                Reasoning = item.Reasoning,
                AttemptNo = attemptNo,
                RubricVersion = req.RubricVersion,
                CreatedAt = DateTime.UtcNow
            });
        }

        // Persist điểm attempt này (giữ answer.Status = Scoring cho tới khi đủ N attempt).
        await _db.SaveChangesAsync(ct);

        // E10 — self-consistency: chỉ chốt answer khi đã đủ N attempt cho rubric_version hiện tại
        //   (đếm distinct attempt_no). Chưa đủ → giữ Scoring, chờ callback attempt kế.
        //   N=1 (mặc định) → 1 attempt là đủ → hành vi cũ.
        var n = Math.Max(1, _scoring.SelfConsistencyN);
        var attemptsForVersion = await _db.AnswerScores.AsNoTracking()
            .Where(s => s.AnswerId == answer.Id && s.RubricVersion == req.RubricVersion)
            .Select(s => s.AttemptNo)
            .Distinct()
            .CountAsync(ct);

        if (attemptsForVersion < n)
        {
            _logger.LogInformation(
                "Answer {AnswerId}: {Got}/{N} attempt (rubric v{Version}) — chờ đủ trước khi Scored",
                answerId, attemptsForVersion, n, req.RubricVersion);
            return;   // giữ Scoring
        }

        // Đủ N → điểm chốt = median/tiêu chí (tính read-time downstream); ở đây đo SPREAD = max−min
        // mỗi tiêu chí giữa các attempt (materialize rồi tính C#) → spread > ngưỡng bất kỳ tiêu chí →
        // needs_review (cờ soi lại). N=1 → spread = 0. E11: kèm cả Reasoning để chấm "nhận xét OK".
        var perAttempt = await _db.AnswerScores.AsNoTracking()
            .Where(s => s.AnswerId == answer.Id && s.RubricVersion == req.RubricVersion)
            .Select(s => new { s.CriterionId, s.Score, s.Reasoning })
            .ToListAsync(ct);

        // E10 — spread giữa các attempt vượt ngưỡng → soi lại.
        var highSpread = perAttempt
            .GroupBy(s => s.CriterionId)
            .Any(g => g.Max(x => x.Score) - g.Min(x => x.Score) > _scoring.VarianceThreshold);

        // E11 — chuẩn "NHẬN XÉT OK" (defense-in-depth): bất kỳ reasoning nào rỗng/quá ngắn (dưới
        // MinReasoningLen ký tự sau trim) → cờ HR soi lại. KHÔNG hard-fail, KHÔNG mất điểm (điểm đã
        // lưu ở loop trên). Opt-in: MinReasoningLen=0 (mặc định) → bỏ qua, giữ hành vi cũ.
        var shortReasoning = _scoring.MinReasoningLen > 0
            && perAttempt.Any(s => (s.Reasoning ?? "").Trim().Length < _scoring.MinReasoningLen);

        var needsReview = highSpread || shortReasoning;

        answer.NeedsReview = needsReview;
        answer.Status = AnswerStatus.Scored;
        await _db.SaveChangesAsync(ct);

        _logger.LogInformation(
            "Answer {AnswerId} -> Scored ({N} attempt, rubric v{Version}), needs_review={NeedsReview}",
            answerId, attemptsForVersion, req.RubricVersion, needsReview);

        await TryCompleteSessionAsync(answer.SessionId, ct);
    }

    // E9 — neo điểm về mức của tiêu chí. Trả (điểm lưu, level_matched).
    //  - Tiêu chí CÓ rubric_levels khai → HARD anchor: score = level.score (snap gần nhất nếu lệch),
    //    KHÔNG drop (INT-9). Ưu tiên levelMatched worker gửi nếu hợp lệ.
    //  - Không khai levels (dải mặc định) → giữ điểm đã kẹp; chỉ lưu levelMatched worker gửi khi
    //    nằm trong [0,maxScore] (tương thích worker cũ + tránh ép integer phá điểm thập phân hợp lệ).
    private (decimal finalScore, int? levelMatched) ResolveLevel(
        RubricCriterion crit, decimal clamped, int? workerLevel, Guid answerId)
    {
        if (crit.Levels is { Count: > 0 })
        {
            var valid = crit.Levels.Select(l => l.Score).Distinct().OrderBy(s => s).ToList();

            int target;
            if (workerLevel is int wl && valid.Contains(wl))
            {
                target = wl;
            }
            else
            {
                // Snap về mức hợp lệ gần điểm nhất (tie-break: mức thấp hơn cho ổn định).
                target = valid.OrderBy(s => Math.Abs(s - clamped)).ThenBy(s => s).First();
                _logger.LogWarning(
                    "Snap điểm criterion {CriterionId} answer {AnswerId} về mức {Level} (điểm kẹp {Clamped}, levelMatched worker {WorkerLevel})",
                    crit.Id, answerId, target, clamped, workerLevel);
            }

            return (target, target);   // E9: score = level.score
        }

        // Dải mặc định: giữ điểm kẹp; lưu levelMatched worker gửi nếu trong [0,maxScore].
        int? lm = workerLevel is int w && w >= 0 && w <= crit.MaxScore ? w : null;
        return (clamped, lm);
    }

    // ── Callback: worker báo chấm thất bại vĩnh viễn ──────────────────────
    public async Task MarkFailedAsync(
        Guid answerId, string? reason, CancellationToken ct = default)
    {
        var answer = await _db.PracticeAnswers
            .FirstOrDefaultAsync(a => a.Id == answerId, ct)
            ?? throw new KeyNotFoundException($"Answer {answerId} không tồn tại");

        // Đã chấm xong rồi (callback trùng/đến muộn) thì không hạ Scored xuống Failed.
        if (answer.Status == AnswerStatus.Scored)
        {
            _logger.LogInformation(
                "Bỏ qua MarkFailed cho answer {AnswerId} vì đã Scored", answerId);
            return;
        }

        answer.Status = AnswerStatus.Failed;
        await _db.SaveChangesAsync(ct);

        _logger.LogWarning(
            "Answer {AnswerId} -> Failed (worker báo lỗi vĩnh viễn). Lý do: {Reason}",
            answerId, reason ?? "(không rõ)");

        // Failed cũng tính là "xong" -> mở đường đóng session đang chờ chấm nốt.
        await TryCompleteSessionAsync(answer.SessionId, ct);
    }

    private async Task TryCompleteSessionAsync(Guid sessionId, CancellationToken ct)
    {
        var session = await _db.PracticeSessions
            .FirstOrDefaultAsync(s => s.Id == sessionId, ct);
        if (session is null) return;

        // Chỉ đóng khi đã submit (Scoring); tránh đóng buổi còn đang làm dở.
        if (session.Status != SessionStatus.Scoring) return;

        var statuses = await _db.PracticeAnswers
            .Where(a => a.SessionId == sessionId)
            .Select(a => a.Status)
            .ToListAsync(ct);

        bool allDone = statuses.All(s =>
            s is AnswerStatus.Scored or AnswerStatus.Skipped or AnswerStatus.Failed);

        if (!allDone) return;

        // PAY-13: chỉ "chấm được" khi có ≥1 answer đạt Scored. Nếu MỌI answer kết thúc Failed/Skipped
        // (scoredCount==0) → buổi không có gì để chấm → phát SessionAbandoned (Payment release, không
        // consume) thay vì SessionScored. Tránh trừ 1 credit cho buổi 0 answer được chấm (PAY-1).
        var scoredCount = statuses.Count(s => s == AnswerStatus.Scored);
        if (scoredCount == 0)
        {
            // DB2: đóng session (state) + ghi outbox-row abandoned CÙNG 1 SaveChanges (atomic) — broker
            // chết vẫn còn row để OutboxDispatcher gửi lại (Payment release), không mất event.
            session.Status = SessionStatus.SessionAbandoned;
            await _scoringNotifier.EnqueueSessionAbandonedAsync(sessionId, "no_scored_answer", ct);
            await _db.SaveChangesAsync(ct);
            _logger.LogInformation(
                "Session {SessionId} -> SessionAbandoned (không answer nào Scored)", sessionId);
            return;
        }

        // DB2: đóng session Scored (state) + ghi outbox-row SessionScored CÙNG 1 SaveChanges (atomic).
        session.Status = SessionStatus.Scored;
        await _scoringNotifier.EnqueueSessionScoredAsync(sessionId, ct);
        await _db.SaveChangesAsync(ct);
        _logger.LogInformation("Session {SessionId} -> Scored", sessionId);

        // BC9/BC10/BC14/BC15: side-effect best-effort SAU khi đã commit (không chặn đóng session).
        await _scoringNotifier.NotifySessionScoredAsync(sessionId, ct);
    }
}