using Isas.InterviewService.ApplicationDbContext;
using Isas.InterviewService.DTOs;
using Isas.InterviewService.Enums;
using Isas.InterviewService.Models;
using Isas.InterviewService.Services.Interfaces;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Options;

namespace Isas.InterviewService.Services;

// Settlement-outbox (Option A) — nửa RECONCILER. Đóng lỗ "publish hụt settlement-event → Payment giữ
// reservation Reserved VĨNH VIỄN": session đã đóng terminal (Scored/SessionAbandoned) trong DB nhưng
// SessionScored/SessionAbandoned KHÔNG lên được RabbitMQ (bus rớt lúc đóng session) → Payment không bao
// giờ consume/release → credit treo. Quét định kỳ session terminal có settlement_published_at còn null
// và phát lại; phát OK → set marker (idempotent, vòng sau bỏ qua).
//
// Payment idempotent theo UNIQUE(credit_reservations.session_id) + ExecuteUpdate guard status (PAY-11)
// nên phát lại CÙNG event KHÔNG double-consume/refund → at-least-once an toàn.
//
// ⚠ CHỈ B2C (CampaignId == null). B2B out-of-scope có chủ đích: session.scored còn nuôi ranking E4 của
// CampaignService (dùng TotalScore), mà B2C/B2B TotalScore KHÔNG được ghi DB Interview — phát lại B2B với
// điểm DỰNG LẠI (reconstruct) sẽ làm lệch ranking. B2C là mối lo credit-leak ở đây và Payment chỉ dùng
// SessionId. Reconcile settlement B2B là follow-up riêng.
//
// Mirror StuckAnswerRepublisher: ScanInterval 2', delay 30s trước lần quét đầu, try/catch mỗi vòng để 1
// lỗi không giết service, scope riêng/lần quét cho DbContext (BackgroundService là singleton), publisher
// singleton inject thẳng, set marker bằng ExecuteUpdate có guard (projection không track entity).
public class SettlementReconciler : BackgroundService
{
    private static readonly TimeSpan ScanInterval = TimeSpan.FromMinutes(2);

    private readonly IServiceScopeFactory _scopeFactory;
    private readonly ISessionEventPublisher _publisher;   // singleton, inject thẳng được
    private readonly ScoringOptions _options;
    private readonly ILogger<SettlementReconciler> _logger;

    public SettlementReconciler(
        IServiceScopeFactory scopeFactory,
        ISessionEventPublisher publisher,
        IOptions<ScoringOptions> options,
        ILogger<SettlementReconciler> logger)
    {
        _scopeFactory = scopeFactory;
        _publisher = publisher;
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
                _logger.LogError(ex, "Lỗi khi đối soát settlement-event chưa phát");
            }

            await Task.Delay(ScanInterval, ct);
        }
    }

    private async Task ScanOnceAsync(CancellationToken ct)
    {
        var graceMinutes = _options.SettlementRepublishGraceMinutes;
        var lookbackHours = _options.SettlementRepublishLookbackHours;
        if (graceMinutes <= 0 || lookbackHours <= 0) return;   // <=0 = TẮT (an toàn: không tự phát lại)

        // BackgroundService là singleton -> phải tạo scope riêng cho DbContext (scoped).
        using var scope = _scopeFactory.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<InterviewDbContext>();

        var now = DateTime.UtcNow;
        var graceCutoff = now.AddMinutes(-graceMinutes);      // đóng session cũ hơn mốc này mới xét
        var lookbackCutoff = now.AddHours(-lookbackHours);    // nhưng không cũ hơn mốc này

        // B2C terminal + chưa phát được (marker null) + đã đóng đủ lâu (qua grace) + còn trong lookback.
        var pending = await db.PracticeSessions
            .Where(s => (s.Status == SessionStatus.Scored || s.Status == SessionStatus.SessionAbandoned)
                        && s.CampaignId == null                // CHỈ B2C
                        && s.SettlementPublishedAt == null
                        && s.CompletedAt != null
                        && s.CompletedAt < graceCutoff
                        && s.CompletedAt > lookbackCutoff)
            .Select(s => new PendingSettlement(
                s.Id, s.CandidateId, s.Status, s.OverallScore, s.CompletedAt!.Value))
            .ToListAsync(ct);

        if (pending.Count == 0) return;

        _logger.LogWarning(
            "Phát hiện {Count} session B2C terminal chưa phát settlement-event, đang phát lại", pending.Count);

        foreach (var s in pending)
        {
            try
            {
                if (s.Status == SessionStatus.Scored)
                {
                    await _publisher.PublishSessionScoredAsync(new SessionScoredEvent
                    {
                        SessionId = s.SessionId,
                        CampaignId = null,                     // B2C
                        CandidateId = s.CandidateId,
                        TotalScore = s.OverallScore ?? 0m,     // snapshot BC9 (equal-weight); Payment không dùng
                        ScoredAt = s.CompletedAt
                    }, ct);
                }
                else   // SessionAbandoned
                {
                    await _publisher.PublishSessionAbandonedAsync(new SessionAbandonedEvent
                    {
                        SessionId = s.SessionId,
                        CampaignId = null,
                        CandidateId = s.CandidateId,
                        Reason = "reconciled",
                        AbandonedAt = s.CompletedAt
                    }, ct);
                }

                // Phát OK -> set marker để vòng sau bỏ qua (idempotent). ExecuteUpdate vì dùng projection.
                await db.PracticeSessions
                    .Where(x => x.Id == s.SessionId)
                    .ExecuteUpdateAsync(u => u.SetProperty(x => x.SettlementPublishedAt, now), ct);

                _logger.LogInformation(
                    "Đã phát lại settlement-event ({Status}) cho session {SessionId}", s.Status, s.SessionId);
            }
            catch (Exception ex)
            {
                // Phát lỗi -> để marker null, vòng sau thử lại (giống StuckAnswerRepublisher).
                _logger.LogError(ex, "Phát lại settlement-event thất bại cho session {SessionId}, để vòng sau", s.SessionId);
            }
        }
    }

    private readonly record struct PendingSettlement(
        Guid SessionId, Guid CandidateId, SessionStatus Status, decimal? OverallScore, DateTime CompletedAt);
}
