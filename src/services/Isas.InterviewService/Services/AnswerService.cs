using Isas.InterviewService.ApplicationDbContext;
using Isas.InterviewService.DTOs;
using Isas.InterviewService.Entities;
using Isas.InterviewService.Enums;
using Isas.InterviewService.Services.Interfaces;
using Microsoft.EntityFrameworkCore;

namespace Isas.InterviewService.Services;

public class AnswerService : IAnswerService
{
    private readonly InterviewDbContext _db;
    private readonly IStorageService _storage;
    private readonly IScoringJobPublisher _scoringPublisher;
    private readonly ISessionScoringNotifier _scoringNotifier;
    private readonly ILogger<AnswerService> _logger;

    public AnswerService(
        InterviewDbContext db,
        IStorageService storage,
        IScoringJobPublisher scoringPublisher,
        ISessionScoringNotifier scoringNotifier,
        ILogger<AnswerService> logger)
    {
        _db = db;
        _storage = storage;
        _scoringPublisher = scoringPublisher;
        _scoringNotifier = scoringNotifier;
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

        // 2. Buổi đã kết thúc thì không cho upload thêm
        if (session.Status is SessionStatus.Completed
            or SessionStatus.Scoring or SessionStatus.Scored)
            throw new InvalidOperationException("Buổi đã kết thúc");

        // 3. Câu hỏi thuộc đúng buổi
        var question = await _db.PracticeQuestions
            .FirstOrDefaultAsync(q => q.Id == questionId && q.SessionId == sessionId, ct)
            ?? throw new KeyNotFoundException("Câu hỏi không thuộc buổi này");

        // 4. Tìm answer cũ (retry) - business rule: tối đa 1 answer mỗi câu
        var answer = await _db.PracticeAnswers
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
            answer.AudioObjectKey = storagePath;
            answer.DurationSec = durationSec;
            answer.Status = AnswerStatus.Uploaded;
            answer.Transcript = null;
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
            var query = _db.RubricCriteria.AsNoTracking().Where(c => c.IsActive);
            query = session.CampaignId is Guid campaignId
                ? query.Where(c => c.CampaignId == campaignId)
                : query.Where(c => c.CampaignId == null && c.JobCategory == session.JobCategory);
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

            var job = new ScoringJob
            {
                AnswerId = answer.Id,
                SessionId = session.Id,
                QuestionId = question.Id,
                AudioObjectKey = answer.AudioObjectKey!,
                QuestionContent = question.Content,
                JobCategory = session.JobCategory.ToString(),
                RubricVersion = rubricVersion,
                Criteria = criteria.Select(c => new ScoringCriterionDto
                {
                    CriterionId = c.Id,
                    Name = c.Name,
                    Description = c.Description,
                    MaxScore = c.MaxScore,
                    Weight = c.Weight
                }).ToList()
            };

            await _scoringPublisher.PublishAsync(job, ct);

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

        // Idempotency: worker retry có thể gửi lại cùng attempt+version.
        // Xoá điểm cũ cùng attempt+version rồi ghi lại, tránh nhân đôi.
        const int attemptNo = 1; // self-consistency nhiều attempt làm sau
        var stale = answer.Scores
            .Where(s => s.AttemptNo == attemptNo && s.RubricVersion == req.RubricVersion)
            .ToList();
        if (stale.Count > 0)
            _db.AnswerScores.RemoveRange(stale);

        answer.Transcript = req.Transcript;

        foreach (var item in req.Scores)
        {
            _db.AnswerScores.Add(new AnswerScore
            {
                Id = Guid.NewGuid(),
                AnswerId = answer.Id,
                CriterionId = item.CriterionId,
                Score = item.Score,
                Reasoning = item.Reasoning,
                AttemptNo = attemptNo,
                RubricVersion = req.RubricVersion,
                CreatedAt = DateTime.UtcNow
            });
        }

        answer.Status = AnswerStatus.Scored;
        await _db.SaveChangesAsync(ct);

        _logger.LogInformation(
            "Lưu {Count} điểm cho answer {AnswerId}, status -> Scored",
            req.Scores.Count, answerId);

        await TryCompleteSessionAsync(answer.SessionId, ct);
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

        if (allDone)
        {
            session.Status = SessionStatus.Scored;
            await _db.SaveChangesAsync(ct);
            _logger.LogInformation("Session {SessionId} -> Scored", sessionId);

            // E2: phát SessionScored (campaign_id + điểm tổng) khi session vừa đóng.
            await _scoringNotifier.NotifySessionScoredAsync(sessionId, ct);
        }
    }
}