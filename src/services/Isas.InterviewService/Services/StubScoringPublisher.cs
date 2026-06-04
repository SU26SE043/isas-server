using Isas.InterviewService.Enums;
using Isas.InterviewService.Models;
using Microsoft.EntityFrameworkCore;

namespace Isas.InterviewService.Services;

// Dùng tạm khi chưa có RabbitMQ + AIService.
// Mô phỏng chấm async: chạy nền, chấm điểm fake, ghi về DB, đổi state -> scored.
public class StubScoringPublisher(
    IServiceScopeFactory scopeFactory,
    ILogger<StubScoringPublisher> logger) : IScoringPublisher
{
    public Task PublishAsync(Guid sessionId, CancellationToken ct = default)
    {
        // Fire-and-forget mô phỏng worker xử lý async (KHÔNG block caller)
        _ = Task.Run(async () =>
        {
            try
            {
                await Task.Delay(2000); // giả lập độ trễ chấm điểm
                await ScoreAsync(sessionId);
            }
            catch (Exception ex)
            {
                logger.LogError(ex, "Stub scoring lỗi cho session {SessionId}", sessionId);
            }
        }, CancellationToken.None);

        return Task.CompletedTask;
    }

    private async Task ScoreAsync(Guid sessionId)
    {
        // Tạo scope riêng vì DbContext scoped, không dùng được ở background thread
        using var scope = scopeFactory.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<InterviewDbContext>();

        var session = await db.PracticeSessions
            .Include(s => s.Questions).ThenInclude(q => q.Answer)
            .FirstOrDefaultAsync(s => s.Id == sessionId);

        if (session is null || session.Status != SessionStatus.Submitted)
            return;

        var rng = new Random();
        decimal total = 0;
        var answered = 0;

        foreach (var q in session.Questions)
        {
            if (q.Answer is null) continue;

            var score = rng.Next(50, 96);        // fake 50–95
            q.Answer.Score = score;
            q.Answer.Feedback = "Phản hồi mẫu (stub). Sẽ thay bằng đánh giá của AIService.";
            total += score;
            answered++;
        }

        session.TotalScore = answered > 0 ? Math.Round(total / answered, 2) : 0;
        session.Feedback = "Tổng kết mẫu (stub).";
        session.Status = SessionStatus.Scored;
        session.ScoredAt = DateTime.UtcNow;

        await db.SaveChangesAsync();
        logger.LogInformation("Stub đã chấm xong session {SessionId}: {Score}", sessionId, session.TotalScore);
    }
}