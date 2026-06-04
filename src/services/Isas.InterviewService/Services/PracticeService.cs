using Isas.InterviewService.DTOs;
using Isas.InterviewService.Enums;
using Isas.InterviewService.Models;
using Microsoft.EntityFrameworkCore;

namespace Isas.InterviewService.Services;

public class PracticeService(
    InterviewDbContext db,
    IQuestionGenerator generator,
    IScoringPublisher scoring) : IPracticeService
{
    // ---------- Phase 4: lấy câu hỏi để làm bài ----------
    public async Task<PracticeSessionResponse?> GetSessionAsync(
        Guid userId, Guid sessionId, CancellationToken ct = default)
    {
        var session = await db.PracticeSessions
            .Include(s => s.Questions.OrderBy(q => q.OrderIndex))
                .ThenInclude(q => q.Answer)
            .FirstOrDefaultAsync(s => s.Id == sessionId, ct);

        if (session is null) return null;
        if (session.UserId != userId)
            throw new UnauthorizedAccessException("Phiên không thuộc về người dùng này.");

        return ToResponse(session);
    }

    public async Task<IReadOnlyList<PracticeSessionSummary>> GetHistoryAsync(
        Guid userId, CancellationToken ct = default)
    {
        return await db.PracticeSessions
            .Where(s => s.UserId == userId)
            .OrderByDescending(s => s.CreatedAt)
            .Select(s => new PracticeSessionSummary(
                s.Id, s.JobCategory, s.Status, s.TotalScore, s.CreatedAt, s.ScoredAt))
            .ToListAsync(ct);
    }

    // ---------- Phase 4: nộp câu trả lời 1 câu ----------
    public async Task<PracticeSessionResponse> CreateSessionAsync(
        Guid userId, CreatePracticeSessionRequest request, CancellationToken ct = default)
    {
        // Validate job category
        if (request.JobCategory is not (JobCategory.BA or JobCategory.BE or JobCategory.FE))
            throw new ArgumentException($"JobCategory không hợp lệ: '{request.JobCategory}'.");

        // Nếu có CvFileId, kiểm tra file tồn tại + thuộc về user
        if (request.CvFileId is not null)
        {
            var cvOwned = await db.Files
                .AnyAsync(f => f.Id == request.CvFileId && f.UserId == userId, ct);
            if (!cvOwned)
                throw new ArgumentException("CV không tồn tại hoặc không thuộc về người dùng này.");
        }

        var session = new PracticeSession
        {
            Id = Guid.NewGuid(),
            UserId = userId,
            JobCategory = request.JobCategory,
            Status = SessionStatus.Draft,
            CvFileId = request.CvFileId,
            JdText = request.JdText
        };

        await db.PracticeSessions.AddAsync(session, ct);
        await db.SaveChangesAsync(ct);

        return ToResponse(session);
    }

    public async Task<PracticeSessionResponse> GenerateQuestionsAsync(
        Guid userId, Guid sessionId, CancellationToken ct = default)
    {
        var session = await db.PracticeSessions
                          .Include(s => s.CvFile)
                          .FirstOrDefaultAsync(s => s.Id == sessionId, ct)   // BỎ .Include(s => s.Questions)
                      ?? throw new KeyNotFoundException("Không tìm thấy phiên.");

        if (session.UserId != userId)
            throw new UnauthorizedAccessException("Phiên không thuộc về người dùng này.");

        if (session.Status != SessionStatus.Draft)
            throw new InvalidOperationException(
                $"Chỉ sinh câu hỏi khi phiên đang '{SessionStatus.Draft}'. Hiện tại: '{session.Status}'.");

        var cvText = session.CvFile?.ParsedText;

        var generated = await generator.GenerateAsync(
            session.JobCategory, cvText, session.JdText, ct);

        if (generated.Count == 0)
            throw new InvalidOperationException("Không sinh được câu hỏi nào.");

        // ---- LẦN 1: chỉ update status session ----
        session.Status = SessionStatus.InProgress;
        await db.SaveChangesAsync(ct);   // nếu nổ ở ĐÂY → lỗi do UPDATE session

        // ---- LẦN 2: add questions độc lập ----
        var order = 1;
        var questions = generated.Select(content => new PracticeQuestion
        {
            Id = Guid.NewGuid(),
            SessionId = session.Id,
            OrderIndex = order++,
            Content = content
        }).ToList();

        await db.PracticeQuestions.AddRangeAsync(questions, ct);
        await db.SaveChangesAsync(ct);   // nếu nổ ở ĐÂY → lỗi do INSERT questions

        // Load lại để trả response đầy đủ
        return await GetSessionAsync(userId, sessionId, ct)
               ?? throw new InvalidOperationException("Không load lại được phiên.");
    }


    public async Task<AnswerResponse> SubmitAnswerAsync(
        Guid userId, Guid sessionId, SubmitAnswerRequest request, CancellationToken ct = default)
    {
        var session = await db.PracticeSessions
            .FirstOrDefaultAsync(s => s.Id == sessionId, ct)
            ?? throw new KeyNotFoundException("Không tìm thấy phiên.");

        if (session.UserId != userId)
            throw new UnauthorizedAccessException("Phiên không thuộc về người dùng này.");

        if (session.Status != SessionStatus.InProgress)
            throw new InvalidOperationException(
                $"Chỉ trả lời được khi phiên đang '{SessionStatus.InProgress}'. Hiện tại: '{session.Status}'.");

        // Câu hỏi phải thuộc đúng phiên này
        var question = await db.PracticeQuestions
            .Include(q => q.Answer)
            .FirstOrDefaultAsync(q => q.Id == request.QuestionId && q.SessionId == sessionId, ct)
            ?? throw new KeyNotFoundException("Câu hỏi không thuộc phiên này.");

        // Validate theo loại answer
        ValidateAnswer(request);

        if (question.Answer is null)
        {
            // Tạo mới
            var answer = new PracticeAnswer
            {
                Id = Guid.NewGuid(),
                QuestionId = question.Id,
                SessionId = sessionId,
                AnswerType = request.AnswerType,
                TextContent = request.TextContent,
                AudioFileId = request.AudioFileId
            };
            await db.PracticeAnswers.AddAsync(answer, ct);
            await db.SaveChangesAsync(ct);
            return ToAnswerResponse(answer);
        }
        else
        {
            // Sửa câu trả lời đã có (cho phép sửa trước khi submit)
            question.Answer.AnswerType = request.AnswerType;
            question.Answer.TextContent = request.TextContent;
            question.Answer.AudioFileId = request.AudioFileId;
            await db.SaveChangesAsync(ct);
            return ToAnswerResponse(question.Answer);
        }
    }

    // ---------- Phase 5 (mở đầu): submit toàn phiên ----------
    public async Task SubmitSessionAsync(Guid userId, Guid sessionId, CancellationToken ct = default)
    {
        var session = await db.PracticeSessions
            .Include(s => s.Questions).ThenInclude(q => q.Answer)
            .FirstOrDefaultAsync(s => s.Id == sessionId, ct)
            ?? throw new KeyNotFoundException("Không tìm thấy phiên.");

        if (session.UserId != userId)
            throw new UnauthorizedAccessException("Phiên không thuộc về người dùng này.");

        if (session.Status != SessionStatus.InProgress)
            throw new InvalidOperationException(
                $"Chỉ submit được khi phiên đang '{SessionStatus.InProgress}'.");

        if (session.Questions.Count == 0)
            throw new InvalidOperationException("Phiên chưa có câu hỏi.");

        session.Status = SessionStatus.Submitted;
        session.SubmittedAt = DateTime.UtcNow;
        await db.SaveChangesAsync(ct);

        // Đẩy chấm điểm async (hiện stub, nối RabbitMQ sau)
        await scoring.PublishAsync(sessionId, ct);
    }

    // ---------- Helpers ----------
    private static void ValidateAnswer(SubmitAnswerRequest req)
    {
        switch (req.AnswerType)
        {
            case AnswerType.Text:
                if (string.IsNullOrWhiteSpace(req.TextContent))
                    throw new ArgumentException("Câu trả lời text không được rỗng.");
                break;
            case AnswerType.Audio:
                if (req.AudioFileId is null)
                    throw new ArgumentException("Câu trả lời audio cần AudioFileId.");
                break;
            default:
                throw new ArgumentException($"AnswerType không hợp lệ: '{req.AnswerType}'.");
        }
    }

    private static AnswerResponse ToAnswerResponse(PracticeAnswer a) => new(
        a.Id, a.AnswerType, a.TextContent, a.AudioFileId, a.Score, a.Feedback);

    private static PracticeSessionResponse ToResponse(PracticeSession s) => new(
        s.Id, s.JobCategory, s.Status, s.CvFileId, s.JdText,
        s.TotalScore, s.Feedback, s.CreatedAt, s.SubmittedAt, s.ScoredAt,
        s.Questions
            .OrderBy(q => q.OrderIndex)
            .Select(q => new QuestionResponse(
                q.Id, q.OrderIndex, q.Content,
                q.Answer is null ? null : ToAnswerResponse(q.Answer)))
            .ToList());
}