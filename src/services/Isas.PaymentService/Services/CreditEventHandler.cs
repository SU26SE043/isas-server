using Isas.PaymentService.DTOs;
using Microsoft.Extensions.Options;
using PaymentService.Models;
using System.Text.Json;

namespace Isas.PaymentService.Services
{
    /// <summary>
    /// E7: nghe event session (do <see cref="InterviewEventConsumer"/> đẩy vào sau khi tiêu thụ
    /// RabbitMQ) → tiêu/nhả credit theo routing-key (payment.md §Tiêu credit + §State machine):
    /// <list type="bullet">
    ///   <item><c>session.scored</c> → <see cref="ICreditAccountService.ConsumeAsync"/> (P5, trừ thật).</item>
    ///   <item><c>session.abandoned</c> → <see cref="ICreditAccountService.ReleaseAsync"/> (P6, nhả chỗ giữ).</item>
    ///   <item>key khác → bỏ qua (không tiêu/hoàn oan).</item>
    /// </list>
    /// Idempotency/absorbing (PAY-11) nằm SẴN trong Consume/Release theo <c>session_id</c> → redeliver /
    /// event ra ngoài thứ tự (SessionScored↔SessionAbandoned) được hấp thụ, KHÔNG trừ/hoàn kép.
    /// Handler chỉ route + deserialize, không tự giữ trạng thái → an toàn gọi lại nhiều lần.
    /// </summary>
    public class CreditEventHandler : ICreditEventHandler
    {
        // Nguồn chân lý routing-key — InterviewEventConsumer bind queue dùng chính 2 hằng này.
        public const string SessionScoredRoutingKey = "session.scored";
        public const string SessionAbandonedRoutingKey = "session.abandoned";

        private static readonly JsonSerializerOptions JsonOptions = new()
        {
            PropertyNameCaseInsensitive = true
        };

        private readonly ICreditAccountService _credits;
        private readonly OrphanReconcileSettings _orphanReconcile;
        private readonly ILogger<CreditEventHandler> _logger;

        // PONR1 Risk④ — "generation_failed" = lỗi sinh câu hỏi, LUÔN xảy ra TRƯỚC mốc materialize (PONR1
        // chưa từng consume session này) → release vô điều kiện, mọi thời điểm. Chuỗi này PHẢI khớp
        // PracticeService.GenerationFailedReason bên InterviewService (hợp đồng cross-service, không có gì
        // ép compiler kiểm — cùng lớp rủi ro với các reason string khác của SessionAbandonedEvent).
        private const string GenerationFailedReason = "generation_failed";

        public CreditEventHandler(
            ICreditAccountService credits,
            ILogger<CreditEventHandler> logger,
            // Optional (default null) → test cũ dựng 2 tham số vẫn compile; thiếu → cutover rỗng
            // (ConsumeFromUtc=null) → gate PONR1 coi như CHƯA kích hoạt (an toàn, xem 2c).
            IOptions<OrphanReconcileSettings>? orphanReconcile = null)
        {
            _credits = credits;
            _logger = logger;
            _orphanReconcile = orphanReconcile?.Value ?? new OrphanReconcileSettings();
        }

        public async Task HandleAsync(string routingKey, string json, CancellationToken ct = default)
        {
            switch (routingKey)
            {
                case SessionScoredRoutingKey:
                    {
                        var evt = JsonSerializer.Deserialize<SessionScoredMessage>(json, JsonOptions);
                        if (evt is null || evt.SessionId == Guid.Empty)
                        {
                            _logger.LogWarning(
                                "SessionScored message rỗng/thiếu session_id — bỏ qua: {Json}", json);
                            return;
                        }

                        // Consume best-effort/idempotent (PAY-11): mọi outcome (Consumed/AlreadyFinalized/
                        // NoReservation) đều KHÔNG ném — an toàn ack. Chủ ví lấy từ reservation trong P5.
                        var result = await _credits.ConsumeAsync(evt.SessionId, ct);
                        _logger.LogInformation(
                            "E7 consume session {SessionId} (campaign={CampaignId}) → {Outcome}",
                            evt.SessionId, evt.CampaignId, result.Outcome);
                        return;
                    }

                case SessionAbandonedRoutingKey:
                    {
                        var evt = JsonSerializer.Deserialize<SessionAbandonedMessage>(json, JsonOptions);
                        if (evt is null || evt.SessionId == Guid.Empty)
                        {
                            _logger.LogWarning(
                                "SessionAbandoned message rỗng/thiếu session_id — bỏ qua: {Json}", json);
                            return;
                        }

                        // PONR1 Risk④ — chỉ gate khi reason KHÁC generation_failed (lỗi sinh câu hỏi luôn release vô
                        // điều kiện, mọi thời điểm — session đó chưa từng có cơ hội được consume tại mốc materialize).
                        if (!string.Equals(evt.Reason, GenerationFailedReason, StringComparison.Ordinal)
                            && _orphanReconcile.ConsumeFromUtc is { } mark)
                        {
                            // ConsumeFromUtc CHỈ có giá trị khi ops cấu hình tường minh (không fallback "giờ khởi
                            // động" như nhánh Scored của R1) — null = PONR1 phía Payment coi như CHƯA kích hoạt, rơi
                            // thẳng xuống release như cũ, không suy đoán mốc nào khác (xem phần đã chốt với user).
                            var snapshot = await _credits.GetReservationGateSnapshotAsync(evt.SessionId, ct);
                            if (snapshot is { IsStillReserved: true } && snapshot.CreatedAt >= mark)
                            {
                                // Chỗ giữ vẫn Reserved (inline-consume ở PracticeService hụt) và session thuộc "chế độ
                                // mới" (tạo sau mốc orphanReconcile) → KHÔNG release ở đây. Credit coi như ĐÃ NỢ tại mốc sinh
                                // câu hỏi. Để nguyên Reserved cho R1 (OrphanReservationReconciler, nhánh SessionAbandoned
                                // mới) hoàn tất CONSUME ở lượt quét kế — lưới cuối hoàn tất khoản thu thay vì hoàn nó.
                                _logger.LogWarning(
                                    "PONR1 Risk④: session {SessionId} SessionAbandoned (reason={Reason}) nhưng chỗ giữ " +
                                    "tạo lúc {CreatedAt:o} >= mốc cutover {Mark:o} → KHÔNG release, chờ R1 hoàn tất consume.",
                                    evt.SessionId, evt.Reason, snapshot.CreatedAt, mark);
                                return;
                            }
                        }

                        // Release best-effort/idempotent (PAY-11): Consumed→no-op (không hoàn oan), Released→
                        // idempotent, miss-reserve→no-op. Không ném → an toàn ack.
                        var result = await _credits.ReleaseAsync(evt.SessionId, ct);
                        _logger.LogInformation(
                            "E7 release session {SessionId} (reason={Reason}) → {Outcome}",
                            evt.SessionId, evt.Reason, result.Outcome);
                        return;
                    }

                default:
                    // Queue của Payment chỉ bind 2 key trên; key lạ (mở rộng exchange sau này) → bỏ qua,
                    // KHÔNG tiêu/hoàn oan. Ack ở consumer để không kẹt requeue.
                    _logger.LogInformation(
                        "E7 bỏ qua routing key không xử lý: {RoutingKey}", routingKey);
                    return;
            }
        }
    }
}