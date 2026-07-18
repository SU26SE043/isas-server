using Isas.InterviewService.ApplicationDbContext;
using Isas.InterviewService.DTOs;
using Isas.InterviewService.Services.Interfaces;
using Microsoft.EntityFrameworkCore;

namespace Isas.InterviewService.Services;

// TTS đọc câu hỏi thành tiếng (trợ năng cho ứng viên) — B2C lẫn B2B.
//
// Service này CHỈ làm 2 việc: (1) kiểm quyền + tìm đúng câu hỏi, (2) nhờ AIService đọc.
// KHÔNG trừ credit: credit = 1 lượt phỏng vấn ĐƯỢC AI CHẤM (PAY-1); nghe lại đề bài không
// phải lượt chấm, và tính tiền theo số lần bấm nghe sẽ phạt chính người cần trợ năng.
// KHÔNG ghi DB: cache nằm ở S3 phía AIService, key theo nội dung ⇒ không cần cột/migration.
public class QuestionSpeechService : IQuestionSpeechService
{
    private readonly InterviewDbContext _db;
    private readonly IAiServiceSpeechSynthesizer _synthesizer;

    public QuestionSpeechService(InterviewDbContext db, IAiServiceSpeechSynthesizer synthesizer)
    {
        _db = db;
        _synthesizer = synthesizer;
    }

    public async Task<QuestionSpeech?> GetQuestionSpeechAsync(
        Guid candidateId, Guid sessionId, Guid questionId, CancellationToken ct = default)
    {
        var session = await _db.PracticeSessions
            .AsNoTracking()
            .FirstOrDefaultAsync(s => s.Id == sessionId, ct);

        if (session is null) return null;

        // INT-11 — chỉ chủ buổi. Ném TRƯỚC khi đụng tới câu hỏi để người ngoài không dò được
        // (câu hỏi này có tồn tại không) qua việc phân biệt 403 với 404.
        if (session.CandidateId != candidateId)
            throw new UnauthorizedAccessException("Không phải buổi của bạn");

        // Lọc theo CẢ SessionId: questionId của buổi khác → không tìm thấy → 404 (không đọc
        // trộm đề của buổi khác chỉ vì đoán đúng GUID).
        var question = await _db.PracticeQuestions
            .AsNoTracking()
            .FirstOrDefaultAsync(q => q.Id == questionId && q.SessionId == sessionId, ct);

        if (question is null) return null;

        var text = (question.Content ?? string.Empty).Trim();
        if (text.Length == 0) return null;   // câu hỏi rỗng: không có gì để đọc

        // AI-4: nội dung câu hỏi = DỮ LIỆU, chuyển nguyên văn, không nội suy/ghép chỉ thị nào.
        return await _synthesizer.SynthesizeAsync(text, ct);
    }
}
