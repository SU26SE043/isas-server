using Isas.InterviewService.ApplicationDbContext;
using Isas.InterviewService.DTOs;
using Isas.InterviewService.Entities;
using Isas.InterviewService.Enums;
using Isas.InterviewService.Models;
using Isas.InterviewService.Services.Interfaces;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Options;

namespace Isas.InterviewService.Services;

// I2 (D21): quét định kỳ session `InProgress` đã quá HẠN CHÓT NHẬN BÀI (`Deadline`) — chống reservation
// treo (B2B). Deadline THẬT lấy từ session (B2B = campaigns.expires_at Campaign gửi lúc tạo session;
// B2C = null → không hard-deadline, chỉ giới hạn từng câu). Mirror pattern background-sweep của
// StuckAnswerRepublisher (ScanInterval 2', scope riêng cho DbContext).
//
// Quá Deadline + Ready/InProgress (B2B):
//   • ≥1 answer  → AUTO-SUBMIT (reuse PracticeService.SubmitSessionAsync): chốt sổ → câu trống → Skipped
//                  → Scoring/Scored → consume credit qua SessionScored (E7).
//   • 0  answer  → SessionAbandoned (reuse event E3): phát để Payment release reservation (không consume).
//
// P1-1 — session không có Deadline: nhánh hard-deadline bỏ qua, nhưng session tạo-rồi-bỏ vẫn có thể
// giữ credit reserve VĨNH VIỄN. Thêm nhánh quét KHÔNG-HOẠT-ĐỘNG: session
// Ready/InProgress mà "last-activity" cũ hơn Scoring:B2CInactivityMinutes → SessionAbandoned + phát
// event để Payment release credit. "last-activity" = max(CreatedAt, answer mới nhất) → người đang
// luyện (vừa upload answer) KHÔNG bao giờ bị quét. B2B không deadline cũng đã reserve tại Start nên
// phải đi qua lưới này.
public class SessionAbandonSweeper : BackgroundService
{
    private static readonly TimeSpan ScanInterval = TimeSpan.FromMinutes(2);

    // 0 answer → bỏ ngang: reason ổn định cho Payment/observability.
    private const string AbandonReason = "expired_no_answer";

    // P1-1 — B2C bỏ ngang do không hoạt động (không có hard-deadline). Reason riêng để phân biệt với
    // "expired_no_answer" (B2B quá hạn nhận bài) khi observ/đối soát Payment.
    private const string B2CInactivityReason = "inactivity_timeout";

    private readonly IServiceScopeFactory _scopeFactory;
    private readonly ScoringOptions _options;
    private readonly ILogger<SessionAbandonSweeper> _logger;

    public SessionAbandonSweeper(
        IServiceScopeFactory scopeFactory,
        IOptions<ScoringOptions> options,
        ILogger<SessionAbandonSweeper> logger)
    {
        _scopeFactory = scopeFactory;
        _options = options.Value;
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
        await ScanExpiredB2BAsync(ct);
        await ScanInactiveB2CAsync(ct);
    }

    // B2B — Ready/InProgress + có Deadline THẬT + đã quá hạn nhận bài. `Ready` ở B2B đã
    // reserve credit tại Start; nếu ứng viên đóng tab trước answer đầu tiên thì nó sẽ không bao
    // giờ tự chuyển InProgress. Deadline null vẫn bỏ qua hoàn toàn theo policy B2B đã chốt.
    private async Task ScanExpiredB2BAsync(CancellationToken ct)
    {
        // Đọc danh sách session quá hạn trong 1 scope (projection, không tracking). Xử lý từng session
        // ở scope RIÊNG bên dưới để auto-submit (tracked) không lẫn với read/abandon (ExecuteUpdate).
        List<ExpiredSession> expired;
        using (var scope = _scopeFactory.CreateScope())
        {
            var db = scope.ServiceProvider.GetRequiredService<InterviewDbContext>();
            var now = DateTime.UtcNow;

            expired = await db.PracticeSessions
                .Where(s => (s.Status == SessionStatus.Ready || s.Status == SessionStatus.InProgress)
                            && s.Deadline != null
                            && s.Deadline < now)
                .Select(s => new ExpiredSession(s.Id, s.CampaignId, s.CandidateId))
                .ToListAsync(ct);
        }

        if (expired.Count == 0) return;

        _logger.LogWarning(
            "Phát hiện {Count} session B2B Ready/InProgress quá hạn nhận bài, chốt buổi", expired.Count);

        foreach (var s in expired)
            await FinalizeExpiredSessionAsync(s, ct);
    }

    // P1-1 — session không hoạt động không có hard deadline: Ready/InProgress + Deadline null mà
    // "last-activity" cũ hơn cutoff. last-activity = max(CreatedAt, answer mới nhất): dịch sang SQL là
    // "CreatedAt < cutoff VÀ KHÔNG có answer nào CreatedAt >= cutoff" → người vừa upload answer (đang
    // làm) không lọt lưới. B2C/B2B bỏ ngang thì ABANDON (release credit), KHÔNG auto-submit.
    private async Task ScanInactiveB2CAsync(CancellationToken ct)
    {
        var inactivityMinutes = _options.B2CInactivityMinutes;
        if (inactivityMinutes <= 0) return;   // 0/âm = tắt (an toàn: không tự release)

        List<ExpiredSession> stale;
        using (var scope = _scopeFactory.CreateScope())
        {
            var db = scope.ServiceProvider.GetRequiredService<InterviewDbContext>();
            var cutoff = DateTime.UtcNow.AddMinutes(-inactivityMinutes);

            stale = await db.PracticeSessions
                .Where(s => (s.Status == SessionStatus.Ready || s.Status == SessionStatus.InProgress)
                            && s.Deadline == null
                            && s.CreatedAt < cutoff
                            && !db.PracticeAnswers.Any(a => a.SessionId == s.Id && a.CreatedAt >= cutoff))
                .Select(s => new ExpiredSession(s.Id, s.CampaignId, s.CandidateId))
                .ToListAsync(ct);
        }

        if (stale.Count == 0) return;

        _logger.LogWarning(
            "Phát hiện {Count} session B2C không hoạt động > {Minutes} phút, bỏ ngang để release credit",
            stale.Count, inactivityMinutes);

        foreach (var s in stale)
        {
            using var scope = _scopeFactory.CreateScope();
            var db = scope.ServiceProvider.GetRequiredService<InterviewDbContext>();
            await AbandonAsync(db, s, B2CInactivityReason, ct);
        }
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
            await AbandonAsync(db, s, AbandonReason, ct);
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

    // SessionAbandoned (E3 + P1-1). Guard Status ∈ {Ready, InProgress} trong WHERE (ExecuteUpdate
    // absorbing: 0 row = đã chốt bởi luồng khác → bỏ qua, không double-enqueue/re-sweep). Reason theo
    // caller: B2B quá hạn = "expired_no_answer"; B2C không hoạt động = "inactivity_timeout".
    // (B2B chỉ đẩy session InProgress vào đây → guard nới Ready không đổi hành vi B2B; B2C có thể Ready.)
    //
    // DB2: state-flip (ExecuteUpdate) + ghi outbox-row abandoned CÙNG 1 transaction tường minh (sweeper
    // KHÔNG dùng change-tracker cho state-flip nên phải BeginTransaction để atomic). 0 row → rollback, bỏ
    // qua. Revert lesson (BC14) best-effort SAU commit (không atomic với abandon — chỉ dọn dẹp).
    private async Task AbandonAsync(InterviewDbContext db, ExpiredSession s, string reason, CancellationToken ct)
    {
        var now = DateTime.UtcNow;

        await using var tx = await db.Database.BeginTransactionAsync(ct);

        var updated = await db.PracticeSessions
            .Where(x => x.Id == s.Id
                        && (x.Status == SessionStatus.Ready || x.Status == SessionStatus.InProgress))
            .ExecuteUpdateAsync(u => u
                .SetProperty(x => x.Status, SessionStatus.SessionAbandoned)
                .SetProperty(x => x.CompletedAt, now)
                // DB14 — ExecuteUpdate bỏ qua SaveChanges override → stamp updated_at tường minh.
                .SetProperty(x => x.UpdatedAt, now), ct);

        if (updated == 0)
        {
            await tx.RollbackAsync(ct);   // đã chốt bởi luồng khác → không đóng/không enqueue
            return;
        }

        // Ghi outbox-row (Payment release credit) CÙNG transaction với state-flip: broker chết vẫn còn row
        // để OutboxDispatcher gửi lại (at-least-once; Payment idempotent theo session_id/PAY-11).
        db.OutboxMessages.Add(OutboxMessage.ForAbandoned(new SessionAbandonedEvent
        {
            SessionId = s.Id,
            CampaignId = s.CampaignId,
            CandidateId = s.CandidateId,
            Reason = reason,
            AbandonedAt = now
        }));
        await db.SaveChangesAsync(ct);
        await tx.CommitAsync(ct);

        _logger.LogInformation(
            "Đã đóng SessionAbandoned + ghi outbox cho session {SessionId} (reason={Reason})", s.Id, reason);

        // BC14: session bỏ ngang đang gắn 1 roadmap lesson (Practicing) → trả lesson về Theory +
        // clear session_id để user /start lại được. Best-effort, SAU commit (không atomic với abandon).
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
    }

    private readonly record struct ExpiredSession(Guid Id, Guid? CampaignId, Guid CandidateId);
}
