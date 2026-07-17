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
    private readonly InterviewDbContext _db;
    private readonly IStorageService _storage;
    private readonly IScoringJobPublisher _scoringPublisher;
    private readonly ISessionScoringNotifier _scoringNotifier;
    private readonly ScoringOptions _scoring;   // E10 — self-consistency (N, ngưỡng spread, temp)
    private readonly ILogger<AnswerService> _logger;

    public AnswerService(
        InterviewDbContext db,
        IStorageService storage,
        IScoringJobPublisher scoringPublisher,
        ISessionScoringNotifier scoringNotifier,
        IOptions<ScoringOptions> scoringOptions,
        ILogger<AnswerService> logger)
    {
        _db = db;
        _storage = storage;
        _scoringPublisher = scoringPublisher;
        _scoringNotifier = scoringNotifier;
        _scoring = scoringOptions.Value;
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

        // 8. Chấm dần: publish job ngay sau khi lưu.
        //    Publish lỗi KHÔNG làm hỏng upload — answer đã lưu, để re-publish sau.
        await TryPublishScoringJobAsync(session, question, answer, ct);

        return new UploadAnswerResult(answer.Id, questionId, answer.Status.ToString());
    }

    private async Task TryPublishScoringJobAsync(
        PracticeSession session, PracticeQuestion question, PracticeAnswer answer,
        CancellationToken ct)
    {
        try
        {
            // Nguồn tiêu chí tùy mode (E1): B2B chấm theo tiêu chí campaign, B2C theo rubric nghề.
            // Criteria materialize của campaign cũng mang JobCategory → B2C phải lọc thêm
            // campaign_id IS NULL để không chấm nhầm bằng tiêu chí campaign cùng nghề.
            // E9: nạp kèm rubric_levels (+ anchors) để đưa mức neo vào message chấm.
            var query = _db.RubricCriteria.AsNoTracking()
                .Include(c => c.Levels).ThenInclude(l => l.Anchors)
                .Where(c => c.IsActive);
            if (session.CampaignId is Guid campaignId)
            {
                query = query.Where(c => c.CampaignId == campaignId);
            }
            else
            {
                // BC16: B2C ưu tiên rubric RIÊNG của candidate cho nghề, else seed mặc định (owner null).
                var owner = await B2CRubricScope.ResolveOwnerAsync(_db, session.CandidateId, session.JobCategory, ct);
                query = owner is Guid oid
                    ? query.Where(c => c.CampaignId == null && c.CandidateId == oid && c.JobCategory == session.JobCategory)
                    : query.Where(c => c.CampaignId == null && c.CandidateId == null && c.JobCategory == session.JobCategory);
            }
            var criteria = await query.ToListAsync(ct);

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
                    Temperature = attempt == 1 ? 0d : _scoring.SelfConsistencyTemperature
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