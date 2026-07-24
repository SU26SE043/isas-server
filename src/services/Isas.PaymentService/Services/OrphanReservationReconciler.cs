using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Options;
using PaymentService.Models;

namespace Isas.PaymentService.Services
{
    /// <summary>
    /// DB18 (DB4b) — reconciler bù trừ orphan reservation. Luồng Start reserve-first: reserve credit
    /// (key=session_id, UNIQUE) TRƯỚC khi insert practice_session vào Interview DB. Nếu process CRASH giữa
    /// reserve↔insert, <c>credit_reservations</c> có row <c>Reserved</c> với <c>session_id</c> KHÔNG BAO GIỜ
    /// có session → orphan (giữ credit vĩnh viễn). try/catch release lúc Start chỉ cover in-process exception;
    /// DB4 <c>CreditReservationReconciler</c> đếm orphan là hợp lệ (không release). Đây là hở còn lại.
    ///
    /// Compensation-reconciler NHẸ (KHÔNG full-saga, KHÔNG bảng saga): quét reservation Reserved quá
    /// ngưỡng tuổi → hỏi Interview session nào THỰC SỰ tồn tại → session không tồn tại = orphan → release
    /// (idempotent/absorbing PAY-11 — chỉ release nếu còn Reserved). Bao cả B2B (owner=Org) + B2C
    /// (owner=User) + lesson (mọi session là row practice_sessions) — không phân biệt owner, chỉ theo
    /// session-existence.
    ///
    /// R1 — mở rộng: ca "session TỒN TẠI nhưng đã TERMINAL mà chỗ giữ vẫn <c>Reserved</c>" trước đây
    /// KHÔNG AI DỌN (reconciler chỉ xử lý session không-tồn-tại) → rò credit vĩnh viễn theo cả hai chiều:
    /// <c>SessionAbandoned</c> mà không release = user mất oan 1 credit; <c>Scored</c> mà không consume =
    /// buổi phỏng vấn miễn phí. Nguồn gốc là mất event settle (binding RabbitMQ vắng một cửa sổ, message bị
    /// topic exchange vứt im lặng trong khi outbox đã đóng dấu published) — nhưng lỗ cần vá là ở đây:
    /// KHÔNG có lưới cuối. Nay phân nhánh theo trạng thái Interview trả về (bảng trong <c>ScanOnceAsync</c>).
    ///
    /// AN TOÀN: CHỈ release khi Interview XÁC NHẬN DƯƠNG không-tồn-tại (call thành công + session ∉ existing).
    /// Interview down/lỗi → <c>GetExistingSessionsAsync</c> NÉM → exception nổi khỏi ScanOnceAsync → catch
    /// vòng ngoài → SKIP cả vòng, KHÔNG release ai (tránh release oan reservation hợp lệ khi không xác minh
    /// được). TUYỆT ĐỐI không coi "call lỗi" = "không tồn tại".
    ///
    /// AN TOÀN (R1) — nhánh CONSUME chặt hơn nhánh RELEASE: trước R1 reconciler chỉ hoàn tiền, sai thì còn
    /// cứu được; nay nó TRỪ tiền, sai là mất tiền của người dùng và không tự phục hồi. Nên:
    /// <list type="bullet">
    /// <item>CHỈ consume khi Interview khẳng định DƯƠNG <c>Scored</c> — whitelist một-phần-tử, KHÔNG suy
    /// diễn từ bất cứ dấu hiệu nào khác. KHÔNG có nhánh <c>default → Consume</c>.</item>
    /// <item>Trạng thái lạ / thiếu / rỗng → SKIP (không consume, không release) + log để thấy ca lạ.</item>
    /// <item>Tồn tại đọc từ <c>ExistingIds</c>, KHÔNG từ <c>States</c> — xem <see cref="InterviewSessionsSnapshot"/>.</item>
    /// <item>Mốc <c>ConsumeFromUtc</c> + công tắc <c>ConsumeTerminalScored</c> chặn trừ tiền hồi tố.</item>
    /// </list>
    ///
    /// Mirror <see cref="CreditReservationReconciler"/>: interval config-được, delay khởi động, try/catch
    /// mỗi vòng (1 lỗi KHÔNG giết service), scope-per-scan cho DbContext (BackgroundService = singleton).
    /// </summary>
    public class OrphanReservationReconciler : BackgroundService
    {
        // R1 — trạng thái session Interview (string, GEN-2). Khai tường minh, KHÔNG dùng enum dùng chung:
        // DB-per-service, ref xuyên service là dây lỏng. Đối chiếu Ordinal (tên enum C#, không phụ thuộc culture).
        private const string StatusScored = "Scored";
        private const string StatusSessionAbandoned = "SessionAbandoned";
        private const string StatusFailed = "Failed";

        // Đang bay hợp lệ → SKIP im lặng (không log-spam: mỗi vòng quét đều gặp).
        // ⚠ "Completed" hiện là trạng thái CHẾT — không production site nào GHI nó (chỉ AnswerService đọc
        // để chặn upload). Giữ ở đây cho khớp enum + phòng thủ nếu sau này có site ghi.
        private static readonly HashSet<string> InFlightStatuses = new(StringComparer.Ordinal)
        {
            "GeneratingQuestions", "Ready", "InProgress", "Completed", "Scoring"
        };

        private readonly IServiceScopeFactory _scopeFactory;
        private readonly OrphanReconcileSettings _options;
        private readonly ILogger<OrphanReservationReconciler> _logger;

        // R1 — mốc "từ nay về sau" cho nhánh consume. Không cấu hình → chốt tại lúc DỰNG reconciler
        // (= khởi động service). Chốt một lần, KHÔNG tính lại mỗi vòng: tính lại mỗi vòng thì mốc bò theo
        // thời gian và nhánh consume sẽ không bao giờ bắt được reservation nào (cần created_at ≥ mốc mà
        // đồng thời cũ hơn ngưỡng orphan).
        private readonly DateTime _consumeFromUtc;

        public OrphanReservationReconciler(
            IServiceScopeFactory scopeFactory,
            IOptions<OrphanReconcileSettings> options,
            ILogger<OrphanReservationReconciler> logger)
        {
            _scopeFactory = scopeFactory;
            _options = options.Value;
            _logger = logger;
            _consumeFromUtc = _options.ConsumeFromUtc ?? DateTime.UtcNow;
        }

        protected override async Task ExecuteAsync(CancellationToken ct)
        {
            // Chờ 1 nhịp cho app + Interview khởi động xong trước khi quét lần đầu.
            await Task.Delay(TimeSpan.FromSeconds(30), ct);

            // R1 — mốc consume PHẢI nhìn thấy được trong log: mặc định nó tiến lên sau MỖI lần restart, nên
            // chỗ giữ sinh ngay trước restart có thể không bao giờ được consume. Hở nhỏ nhưng không được im lặng.
            _logger.LogInformation(
                "OrphanReservationReconciler: nhánh consume {State}, mốc ConsumeFromUtc={Mark:o} ({Source}). " +
                "Chỗ giữ Scored có created_at < mốc sẽ SKIP (đối soát tay, không trừ hồi tố).",
                _options.ConsumeTerminalScored ? "BẬT" : "TẮT",
                _consumeFromUtc,
                _options.ConsumeFromUtc.HasValue ? "cấu hình tường minh" : "mốc khởi động dịch vụ");

            var interval = TimeSpan.FromSeconds(_options.ScanIntervalSeconds > 0 ? _options.ScanIntervalSeconds : 120);

            while (!ct.IsCancellationRequested)
            {
                try
                {
                    await ScanOnceAsync(ct);
                }
                catch (Exception ex)
                {
                    // Không để 1 vòng lỗi (kể cả Interview down → InterviewServiceException) giết background
                    // service. Skip vòng = KHÔNG release ai (an toàn: không xác minh được thì không đụng).
                    _logger.LogError(ex, "Lỗi khi đối soát orphan reservation (bỏ qua vòng này, KHÔNG release)");
                }

                await Task.Delay(interval, ct);
            }
        }

        // private + gọi qua reflection trong test (idiom repo: CreditReservationReconciler/SettlementReconciler).
        private async Task ScanOnceAsync(CancellationToken ct)
        {
            if (!_options.Enabled) return;   // safe-disable

            using var scope = _scopeFactory.CreateScope();
            var db = scope.ServiceProvider.GetRequiredService<PaymentDbContext>();

            var thresholdMinutes = _options.OrphanThresholdMinutes > 0 ? _options.OrphanThresholdMinutes : 10;
            var batchSize = _options.BatchSize > 0 ? _options.BatchSize : 200;
            var cutoff = DateTime.UtcNow.AddMinutes(-thresholdMinutes);

            // Ứng viên: Reserved + quá ngưỡng tuổi (chưa quá ngưỡng = có thể insert đang dở → bỏ qua).
            // R1 — lấy kèm CreatedAt để đối chiếu mốc ConsumeFromUtc ở nhánh consume.
            var candidates = await db.CreditReservations
                .Where(r => r.Status == ReservationStatus.Reserved && r.CreatedAt < cutoff)
                .OrderBy(r => r.CreatedAt)
                .Take(batchSize)
                .Select(r => new { r.SessionId, r.CreatedAt })
                .ToListAsync(ct);

            if (candidates.Count == 0) return;

            var sessionIds = candidates.Select(c => c.SessionId).ToList();

            // XÁC MINH DƯƠNG với Interview. NÉM (Interview down) → nổi ra ngoài → skip vòng, KHÔNG đụng ai.
            var interview = scope.ServiceProvider.GetRequiredService<IInterviewSessionClient>();
            var snapshot = await interview.GetExistingSessionsAsync(sessionIds, ct);

            var accountService = scope.ServiceProvider.GetRequiredService<ICreditAccountService>();
            var released = 0;
            var consumed = 0;

            foreach (var candidate in candidates)
            {
                var sessionId = candidate.SessionId;

                // (a) TỒN TẠI đọc từ ExistingIds — KHÔNG từ States. States rỗng (Interview bản cũ) chỉ được
                //     phép làm ta SKIP, không được biến mọi session thành "không tồn tại" → release oan.
                if (!snapshot.ExistingIds.Contains(sessionId))
                {
                    // Orphan cũ (DB18): crash giữa reserve↔insert lúc Start → session KHÔNG BAO GIỜ tồn tại.
                    if (await TryReleaseAsync(accountService, sessionId, "session không tồn tại", ct))
                        released++;
                    continue;
                }

                // (b) Session TỒN TẠI mà thiếu trạng thái (Interview chưa có R1, hoặc status rỗng) → SKIP.
                //     "Không biết" KHÔNG được suy thành bất cứ hành động tiền nào.
                if (!snapshot.States.TryGetValue(sessionId, out var status) || string.IsNullOrWhiteSpace(status))
                {
                    _logger.LogWarning(
                        "Session {SessionId} tồn tại nhưng Interview không trả trạng thái (bản cũ?) → SKIP, " +
                        "không consume/release", sessionId);
                    continue;
                }

                // (c) CHỈ khẳng định dương "Scored" mới TRỪ TIỀN. Whitelist một-phần-tử, không suy diễn.
                if (string.Equals(status, StatusScored, StringComparison.Ordinal))
                {
                    if (await TryConsumeScoredAsync(accountService, sessionId, candidate.CreatedAt, ct))
                        consumed++;
                    continue;
                }

                // (d) Terminal "không chấm được gì" → hoàn chỗ giữ. SessionAbandoned = bỏ ngang (E7);
                //     Failed = lỗi sinh câu hỏi (BK12 vốn phát SessionAbandoned để release).
                if (string.Equals(status, StatusFailed, StringComparison.Ordinal))
                {
                    if (await TryReleaseAsync(accountService, sessionId, $"session {status}", ct))
                        released++;
                    continue;
                }

                // (d') SessionAbandoned = bỏ ngang SAU KHI có thể đã được consume tại mốc sinh câu hỏi (PONR1).
                //      Dùng _options.ConsumeFromUtc THÔ (KHÔNG dùng _consumeFromUtc đã fallback "giờ khởi động"
                //      của nhánh (c) — nhánh (c) là tính năng CŨ đã sống, còn nhánh này PHẢI "dark" đúng nghĩa cho
                //      tới khi ops cấu hình tường minh, nếu không R1 sẽ tự trừ tiền no-show hợp lệ ngay khi
                //      PaymentService restart — kể cả TRƯỚC KHI Interview bật Billing:ConsumeAtQuestionGeneration.
                if (string.Equals(status, StatusSessionAbandoned, StringComparison.Ordinal))
                {
                    if (_options.ConsumeFromUtc is { } mark && candidate.CreatedAt >= mark)
                    {
                        if (await TryConsumeAbandonedPastCutoverAsync(accountService, sessionId, candidate.CreatedAt, ct))
                            consumed++;
                        continue;
                    }

                    // Trước mốc cutover (hoặc PONR1 phía Payment chưa kích hoạt — ConsumeFromUtc chưa cấu hình) →
                    // hành vi CŨ, hoàn chỗ giữ.
                    if (await TryReleaseAsync(accountService, sessionId, $"session {status}", ct))
                        released++;
                    continue;
                }

                // (e) Đang bay hợp lệ → SKIP im lặng (gặp mỗi vòng, log sẽ thành nhiễu).
                if (InFlightStatuses.Contains(status)) continue;

                // (f) FAIL-SAFE: trạng thái lạ (Interview thêm state mới, hoặc dây hỏng) → SKIP + log.
                //     KHÔNG consume, KHÔNG release. Đây là lý do KHÔNG có nhánh `default → Consume`.
                _logger.LogWarning(
                    "Session {SessionId} trả trạng thái lạ '{Status}' → SKIP (không consume/release). " +
                    "Có thể Interview đã thêm trạng thái mới mà Payment chưa biết.", sessionId, status);
            }

            if (released > 0)
                _logger.LogWarning(
                    "OrphanReservationReconciler: release {Count} chỗ giữ (session không tồn tại, hoặc đã " +
                    "SessionAbandoned/Failed mà chưa được settle)", released);

            if (consumed > 0)
                _logger.LogWarning(
                    "OrphanReservationReconciler: CONSUME {Count} chỗ giữ của session đã Scored (máy tự trừ " +
                    "credit — buổi đã được AI chấm, PAY-1/PAY-13; event settle đã mất)", consumed);
        }

        // R1 — nhánh TRỪ TIỀN, gác 3 lớp trước khi chạm ví. Trả true nếu thực sự consume.
        private async Task<bool> TryConsumeScoredAsync(
            ICreditAccountService accountService, Guid sessionId, DateTime reservationCreatedAt, CancellationToken ct)
        {
            // Lớp 1 — công tắc riêng: tắt được nhánh trừ tiền mà vẫn giữ nhánh release chạy.
            if (!_options.ConsumeTerminalScored)
            {
                _logger.LogWarning(
                    "Session {SessionId} đã Scored mà chỗ giữ còn Reserved, nhưng ConsumeTerminalScored=false " +
                    "→ SKIP (chỗ giữ vẫn treo cho tới khi bật lại hoặc đối soát tay)", sessionId);
                return false;
            }

            // Lớp 2 — MỐC TUYỆT ĐỐI: không trừ hồi tố. Chỗ giữ tồn đọng là hệ quả sự cố hạ tầng của chúng
            // ta, để NGƯỜI đối soát (OPS2). ⚠ KHÔNG release ở đây: release một buổi ĐÃ CHẤM = tặng buổi
            // miễn phí, đúng cái bug R1 đang sửa.
            if (reservationCreatedAt < _consumeFromUtc)
            {
                _logger.LogWarning(
                    "Session {SessionId} đã Scored mà chỗ giữ còn Reserved, nhưng chỗ giữ tạo lúc {CreatedAt:o} " +
                    "< mốc ConsumeFromUtc {Mark:o} → SKIP, KHÔNG trừ hồi tố và KHÔNG release. Cần đối soát tay.",
                    sessionId, reservationCreatedAt, _consumeFromUtc);
                return false;
            }

            try
            {
                // Lớp 3 — ConsumeAsync absorbing (PAY-11): chỉ tác dụng khi còn Reserved; đã Consumed/Released
                // → no-op, KHÔNG trừ lần 2. An toàn khi đua với InterviewEventConsumer (E7) — nhưng KHÔNG
                // dựa vào tính chất đó để nới lỏng 2 lớp gác trên.
                var result = await accountService.ConsumeAsync(sessionId, ct);
                return result.Outcome == ConsumeOutcome.Consumed;
            }
            catch (Exception ex)
            {
                _logger.LogError(ex,
                    "Không thể consume chỗ giữ của session {SessionId} (đã Scored), bỏ qua", sessionId);
                return false;
            }
        }

        // Trả true nếu thực sự release. ReleaseAsync idempotent/absorbing (PAY-11): chỉ release nếu còn
        // Reserved; đã Consumed/Released → no-op (KHÔNG hoàn oan). Owner lấy từ reservation (nguồn chân lý).
        private async Task<bool> TryReleaseAsync(
            ICreditAccountService accountService, Guid sessionId, string reason, CancellationToken ct)
        {
            try
            {
                var result = await accountService.ReleaseAsync(sessionId, ct);
                return result.Outcome == ReleaseOutcome.Released;
            }
            catch (Exception ex)
            {
                // Lỗi 1 reservation → bỏ qua nó, KHÔNG giết cả vòng (các ứng viên khác vẫn xử lý).
                _logger.LogError(ex,
                    "Không thể release chỗ giữ session {SessionId} ({Reason}), bỏ qua", sessionId, reason);
                return false;
            }
        }

        // R1 Risk④/PONR1 — nhánh MỚI: session SessionAbandoned (no-show/hết hạn/không hoạt động — KHÔNG
        // phải lỗi sinh câu hỏi) mà chỗ giữ vẫn Reserved và được tạo SAU mốc cutover PONR1 → PONR1 lẽ ra đã
        // consume tại mốc sinh câu hỏi nhưng inline-consume (PracticeService.ConsumeQuietlyAsync) đã hụt.
        // R1 hoàn tất khoản thu đó Ở ĐÂY thay vì hoàn nó — đúng vai trò lưới cuối của reconciler này.
        //
        // Dùng CHUNG 3 lớp gác với TryConsumeScoredAsync (không tách bản sao logic) — chỉ khác chỗ gọi.
        // ⚠ Log bên trong TryConsumeScoredAsync ghi "đã Scored" — với nhánh này câu chữ không hoàn toàn
        // đúng (session thực ra là SessionAbandoned), nhưng hành vi/số liệu 100% đúng. Muốn log chuẩn xác
        // hơn thì cần đổi chữ ký TryConsumeScoredAsync — rủi ro phá test reflection hiện có, để sau nếu cần.
        private Task<bool> TryConsumeAbandonedPastCutoverAsync(
            ICreditAccountService accountService, Guid sessionId, DateTime reservationCreatedAt, CancellationToken ct)
            => TryConsumeScoredAsync(accountService, sessionId, reservationCreatedAt, ct);
    }
}
