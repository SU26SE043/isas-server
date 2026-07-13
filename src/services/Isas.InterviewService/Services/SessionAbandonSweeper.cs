using Isas.InterviewService.ApplicationDbContext;
using Isas.InterviewService.DTOs;
using Isas.InterviewService.Enums;
using Isas.InterviewService.Services.Interfaces;
using Microsoft.EntityFrameworkCore;

namespace Isas.InterviewService.Services;

// I2 (D21): quét định kỳ session `InProgress` đã quá HẠN CHÓT NHẬN BÀI (`Deadline`) — chống reservation
// treo (B2B). Deadline THẬT lấy từ session (B2B = campaigns.expires_at Campaign gửi lúc tạo session;
// B2C = null → không hard-deadline, chỉ giới hạn từng câu). Mirror pattern background-sweep của
// StuckAnswerRepublisher (ScanInterval 2', scope riêng cho DbContext).
//
// Quá Deadline + InProgress:
//   • ≥1 answer  → AUTO-SUBMIT (reuse PracticeService.SubmitSessionAsync): chốt sổ → câu trống → Skipped
//                  → Scoring/Scored → consume credit qua SessionScored (E7).
//   • 0  answer  → SessionAbandoned (reuse event E3): phát để Payment release reservation (không consume).
//   • Deadline == null (B2C) → KHÔNG đụng (không có hard-deadline).
public class SessionAbandonSweeper : BackgroundService
{
    private static readonly TimeSpan ScanInterval = TimeSpan.FromMinutes(2);

    // 0 answer → bỏ ngang: reason ổn định cho Payment/observability.
    private const string AbandonReason = "expired_no_answer";

    private readonly IServiceScopeFactory _scopeFactory;
    private readonly ISessionEventPublisher _eventPublisher;
    private readonly ILogger<SessionAbandonSweeper> _logger;

    public SessionAbandonSweeper(
        IServiceScopeFactory scopeFactory,
        ISessionEventPublisher eventPublisher,
        ILogger<SessionAbandonSweeper> logger)
    {
        _scopeFactory = scopeFactory;
        _eventPublisher = eventPublisher;
        _logger = logger;
    }

    protected override async Task ExecuteAsync(CancellationToken ct)
    {
        // Chờ 1 nhịp trước khi quét lần đầu, để app khởi động xong.
        await Task.Delay(TimeSpan.FromSeconds(30), ct);

        while (!ct.IsCancellationRequested)
        {
            try
            {
                await ScanOnceAsync(ct);
            }
            catch (Exception ex)
            {
                // Không để 1 vòng lỗi giết cả background service.
                _logger.LogError(ex, "Lỗi khi quét session quá hạn nhận bài");
            }

            await Task.Delay(ScanInterval, ct);
        }
    }

    private async Task ScanOnceAsync(CancellationToken ct)
    {
        // Đọc danh sách session quá hạn trong 1 scope (projection, không tracking). Xử lý từng session
        // ở scope RIÊNG bên dưới để auto-submit (tracked) không lẫn với read/abandon (ExecuteUpdate).
        List<ExpiredSession> expired;
        using (var scope = _scopeFactory.CreateScope())
        {
            var db = scope.ServiceProvider.GetRequiredService<InterviewDbContext>();
            var now = DateTime.UtcNow;

            // InProgress + có Deadline THẬT + đã quá hạn. Deadline null (B2C) → bỏ qua hoàn toàn.
            expired = await db.PracticeSessions
                .Where(s => s.Status == SessionStatus.InProgress
                            && s.Deadline != null
                            && s.Deadline < now)
                .Select(s => new ExpiredSession(s.Id, s.CampaignId, s.CandidateId))
                .ToListAsync(ct);
        }

        if (expired.Count == 0) return;

        _logger.LogWarning(
            "Phát hiện {Count} session InProgress quá hạn nhận bài, chốt buổi", expired.Count);

        foreach (var s in expired)
            await FinalizeExpiredSessionAsync(s, ct);
    }

    private async Task FinalizeExpiredSessionAsync(ExpiredSession s, CancellationToken ct)
    {
        // Scope riêng/session: auto-submit dùng change tracker của PracticeService; abandon dùng
        // ExecuteUpdate. Tách scope để tránh state lẫn nhau giữa các session.
        using var scope = _scopeFactory.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<InterviewDbContext>();

        var hasAnswer = await db.PracticeAnswers.AnyAsync(a => a.SessionId == s.Id, ct);

        if (hasAnswer)
            await AutoSubmitAsync(scope, s, ct);
        else
            await AbandonAsync(db, s, ct);
    }

    // ≥1 answer → auto-submit: reuse SubmitSessionAsync (chốt sổ + câu trống Skipped + đóng Scoring/Scored
    // + phát SessionScored). Best-effort: race đổi status giữa SELECT và submit → InvalidOperationException
    // (SubmitSession guard status) → nuốt + log (vòng sau/luồng khác đã lo).
    private async Task AutoSubmitAsync(IServiceScope scope, ExpiredSession s, CancellationToken ct)
    {
        try
        {
            var practice = scope.ServiceProvider.GetRequiredService<IPracticeService>();
            await practice.SubmitSessionAsync(s.CandidateId, s.Id, ct);
            _logger.LogInformation("Auto-submit session quá hạn {SessionId} (≥1 answer)", s.Id);
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Auto-submit session quá hạn {SessionId} thất bại", s.Id);
        }
    }

    // 0 answer → SessionAbandoned (E3). Guard Status == InProgress trong WHERE (ExecuteUpdate absorbing:
    // 0 row = đã chốt bởi luồng khác → bỏ qua). Revert lesson gắn kèm (BC14) + phát event (Payment release).
    private async Task AbandonAsync(InterviewDbContext db, ExpiredSession s, CancellationToken ct)
    {
        var now = DateTime.UtcNow;

        var updated = await db.PracticeSessions
            .Where(x => x.Id == s.Id && x.Status == SessionStatus.InProgress)
            .ExecuteUpdateAsync(u => u
                .SetProperty(x => x.Status, SessionStatus.SessionAbandoned)
                .SetProperty(x => x.CompletedAt, now), ct);

        if (updated == 0) return;

        // BC14: session bỏ ngang đang gắn 1 roadmap lesson (Practicing) → trả lesson về Theory +
        // clear session_id để user /start lại được. Release credit do E7 lo qua event dưới. Best-effort.
        try
        {
            await db.RoadmapLessons
                .Where(l => l.SessionId == s.Id && l.Status == LessonStatus.Practicing)
                .ExecuteUpdateAsync(u => u
                    .SetProperty(l => l.Status, LessonStatus.Theory)
                    .SetProperty(l => l.SessionId, (Guid?)null), ct);
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "BC14: revert lesson về Theory thất bại cho session {SessionId}", s.Id);
        }

        var evt = new SessionAbandonedEvent
        {
            SessionId = s.Id,
            CampaignId = s.CampaignId,
            CandidateId = s.CandidateId,
            Reason = AbandonReason,
            AbandonedAt = now
        };

        try
        {
            await _eventPublisher.PublishSessionAbandonedAsync(evt, ct);
            _logger.LogInformation("Đã phát SessionAbandoned cho session {SessionId} (0 answer)", s.Id);
        }
        catch (Exception ex)
        {
            // Publish lỗi KHÔNG được làm hỏng việc đóng session — session đã Abandoned trong DB rồi
            // (giống pattern nuốt lỗi publish ở SessionScoringNotifier/E2). Miss event tạm làm Payment lệch.
            _logger.LogError(ex, "Phát SessionAbandoned thất bại cho session {SessionId}", s.Id);
        }
    }

    private readonly record struct ExpiredSession(Guid Id, Guid? CampaignId, Guid CandidateId);
}
