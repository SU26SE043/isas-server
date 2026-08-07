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
    private readonly IAiServiceInterviewDecider? _decider;   // phỏng vấn THÍCH ỨNG (null = tắt / test cũ)
    private readonly AdaptiveOptions _adaptive;   // INT-17b — chỉ đọc trần số lần lỗi (phần còn lại đóng dấu trên session)
    private readonly ILogger<AnswerService> _logger;

    public AnswerService(
        InterviewDbContext db,
        IStorageService storage,
        IScoringJobPublisher scoringPublisher,
        ISessionScoringNotifier scoringNotifier,
        IOptions<ScoringOptions> scoringOptions,
        ILogger<AnswerService> logger,
        // Optional (default null) → mọi test dựng AnswerService cũ (6 tham số) vẫn compile + adaptive tắt;
        // DI inject bản thật khi đăng ký (AddHttpClient). Adaptive chỉ chạy khi decider != null VÀ session bật.
        IAiServiceInterviewDecider? decider = null,
        // Optional cùng lý do: null → mặc định AdaptiveOptions (trần lỗi 3). Trần/toggle của BUỔI đọc từ
        // session (đã đóng dấu lúc tạo), không đọc lại config ở đây — buổi đang chạy không bị đổi luật giữa chừng.
        IOptions<AdaptiveOptions>? adaptiveOptions = null)
    {
        _db = db;
        _storage = storage;
        _scoringPublisher = scoringPublisher;
        _scoringNotifier = scoringNotifier;
        _scoring = scoringOptions.Value;
        _decider = decider;
        _adaptive = adaptiveOptions?.Value ?? new AdaptiveOptions();
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
            // DB31: 1 JOIN ⇒ transcript (TEXT) của answer lặp trên MỌI dòng score; ×N khi bật
            // self-consistency (SelfConsistencyN). Split query đọc answer 1 lần, scores 1 lần.
            .AsSplitQuery()
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
            // Con dấu engine đi CẶP với transcript: giữ lại sau khi thu âm lại là khai lai lịch của
            // một bản chép không còn tồn tại — và nó sẽ được đọc như thể mô tả bản chép MỚI (cùng lý
            // do bản vá F11 phải xoá cụm chỉ số ở ngay dưới).
            answer.TranscriptEngine = null;
            if (answer.Scores.Count > 0)
                _db.AnswerScores.RemoveRange(answer.Scores);
            answer.NeedsReview = false;
            // F13 — gợi ý câu trả lời mẫu bám câu trả lời CŨ ("bù chỗ bạn còn thiếu"), giữ lại
            // sau khi thu âm lại là hiển thị lời khuyên cho một bài không còn tồn tại.
            answer.SampleAnswer = null;
            // F11 — chỉ số cách nói đo trên bản ghi âm CŨ; giữ lại sau khi thu âm lại là báo
            // "bạn nói 'ừm' 12 lần" cho một bản thu không còn tồn tại.
            DeliveryMetricsMapper.Apply(answer, null);
        }

        // Câu trả lời đầu tiên -> session Ready chuyển InProgress.
        if (session.Status == SessionStatus.Ready)
            session.Status = SessionStatus.InProgress;

        await _db.SaveChangesAsync(ct);

        _logger.LogInformation(
            "Saved answer {AnswerId} for question {QuestionId} in session {SessionId}",
            answer.Id, questionId, sessionId);

        // Phỏng vấn THÍCH ỨNG — answer đã durable (SaveChanges trên) TRƯỚC mọi call AIService → decide lỗi
        // KHÔNG làm mất câu trả lời. Chỉ chạy khi bật (decider != null + session.AdaptiveEnabled). Trả về
        // transcript đồng bộ (đã lưu lên answer) + câu hỏi kế (nếu có) để (a) đẩy transcript vào ScoringJob
        // → worker bỏ Whisper, (b) client hiện câu kế ngay trong response (không cần poll GET).
        var adaptive = await TryRunAdaptiveAsync(session, question, answer, ct);

        // 8. Chấm dần: publish job ngay sau khi lưu (kèm transcript đồng bộ nếu adaptive đã transcribe).
        //    Publish lỗi KHÔNG làm hỏng upload — answer đã lưu, để re-publish sau.
        await TryPublishScoringJobAsync(session, question, answer, ct);

        return new UploadAnswerResult(
            answer.Id, questionId, answer.Status.ToString(),
            Transcript: answer.Transcript,
            NextAction: adaptive.Action,
            NextQuestion: adaptive.AppendedQuestion is null ? null : new NextQuestionResponse(
                adaptive.AppendedQuestion.Id, adaptive.AppendedQuestion.OrderNo,
                adaptive.AppendedQuestion.Content, adaptive.AppendedQuestion.TimeLimitSec,
                adaptive.AppendedQuestion.Kind.ToString()),
            InterviewComplete: adaptive.InterviewComplete);
    }

    // ── Phỏng vấn THÍCH ỨNG: transcribe đồng bộ + quyết định + append câu kế ──────────────
    private sealed record AdaptiveOutcome(
        string? Action, PracticeQuestion? AppendedQuestion, bool InterviewComplete)
    {
        // Không chạy adaptive (tắt / chưa tới frontier / decide lỗi) → không có tín hiệu, không "complete".
        public static readonly AdaptiveOutcome None = new(null, null, false);
    }

    // Chạy vòng thích ứng SAU khi answer đã lưu durable. Bọc toàn bộ trong try/catch → mọi lỗi (kể cả
    // đua unique index khi double-POST) chỉ log + trả None: upload LUÔN thành công, degrade về luồng tĩnh.
    private async Task<AdaptiveOutcome> TryRunAdaptiveAsync(
        PracticeSession session, PracticeQuestion question, PracticeAnswer answer, CancellationToken ct)
    {
        if (_decider is null || !session.AdaptiveEnabled)
            return AdaptiveOutcome.None;

        var perQuestionMode = session.MaxDeepPerQuestion > 0;

        try
        {
            // (0) INT-17b — chống chờ chết: chế độ chuỗi gọi AI sau GẦN NHƯ MỌI câu trả lời, nên khi
            // AIService hỏng thì mỗi lượt upload vẫn phải chờ hết timeout decider. Chạm trần lỗi →
            // thôi gọi, degrade hẳn về luồng tĩnh (answer vẫn lưu bình thường).
            if (perQuestionMode && _adaptive.MaxFailuresPerSession > 0)
            {
                // ⚠ Đọc THẲNG từ DB, KHÔNG đọc `session.AdaptiveFailures`: bộ đếm được cộng bằng
                // `ExecuteUpdate` (atomic, không đụng change tracker) nên entity đang được theo dõi vẫn
                // giữ giá trị cũ. Trong production mỗi request một DbContext nên đọc lại là ra đúng, nhưng
                // nếu cùng một context xử lý nhiều lượt thì cổng này im lặng KHÔNG BAO GIỜ đóng.
                var failures = await _db.PracticeSessions.AsNoTracking()
                    .Where(s => s.Id == session.Id)
                    .Select(s => s.AdaptiveFailures)
                    .FirstOrDefaultAsync(ct);
                if (failures >= _adaptive.MaxFailuresPerSession)
                {
                    _logger.LogWarning(
                        "Adaptive: session {SessionId} đã lỗi {Failures} lần — thôi gọi decide-next (degrade tĩnh)",
                        session.Id, failures);
                    return AdaptiveOutcome.None;
                }
            }

            // Số câu CHƯA có answer. `answer` vừa SaveChanges ở trên nên đã tính vào truy vấn này.
            // Dùng cho cả hai chế độ: chế độ cũ lấy làm điều kiện frontier; chế độ chuỗi lấy để biết
            // "hết hội thoại" có đồng nghĩa "xong buổi" hay không.
            var pendingCount = await _db.PracticeQuestions
                .CountAsync(q => q.SessionId == session.Id
                                 && !_db.PracticeAnswers.Any(a => a.QuestionId == q.Id), ct);

            // (1) CHẾ ĐỘ CŨ (MaxDeepPerQuestion = 0): chỉ append khi MỌI câu hiện tại đã có answer
            // (frontier tuyến tính, độc lập thứ tự trả lời).
            //     CHẾ ĐỘ CHUỖI (INT-17b): bỏ frontier — mỗi câu tự mọc chuỗi đào sâu của nó ngay sau khi
            // được trả lời, nên trả lời câu nào là đào sâu câu đó. Điều kiện thay thế: chuỗi chứa câu vừa
            // trả lời còn dưới trần độ sâu.
            //     ⚠ Frontier cũ kiêm luôn việc chặn sinh trùng khi re-upload; bỏ nó KHÔNG hở, vì (2) khoá
            // theo `answer.Id` mà Id đó GIỮ NGUYÊN qua re-upload (xem `answerId` ở đầu UploadAnswerAsync).
            if (!perQuestionMode && pendingCount > 0)
                return AdaptiveOutcome.None;

            if (perQuestionMode && question.Depth >= session.MaxDeepPerQuestion)
            {
                // Chuỗi này hết độ sâu → chuyển sang câu gốc kế (nếu còn). KHÔNG gọi AI.
                // Phải trả `InterviewComplete` theo pendingCount chứ không trả None: câu CUỐI CÙNG chạm
                // trần độ sâu thì buổi đã xong thật, không báo thì ứng viên không được mời nộp bài.
                return EndOutcome("end", pendingCount);
            }

            // (2) Idempotency: answer này đã "đẻ" câu kế rồi (re-upload) → không sinh trùng (unique index backstop).
            var alreadyHasChild = await _db.PracticeQuestions
                .AnyAsync(q => q.GeneratedFromAnswerId == answer.Id, ct);
            if (alreadyHasChild)
                return AdaptiveOutcome.None;

            // (3) Ngân sách buổi — hết trần câu / trần thích ứng → kết thúc, KHÔNG gọi AI (tiết kiệm latency/cost).
            // ⚠ INT-17b: KHÔNG còn trả cứng `InterviewComplete: true`. Ở chế độ chuỗi, hết ngân sách lúc
            // vẫn còn 3 câu gốc chưa trả lời mà báo "xong" thì hoá ra giục ứng viên nộp bài giữa chừng.
            var askedCount = await _db.PracticeQuestions.CountAsync(q => q.SessionId == session.Id, ct);
            var followUpCount = await _db.PracticeQuestions
                .CountAsync(q => q.SessionId == session.Id && q.Kind != QuestionKind.Seed, ct);
            var budgetLeft = (session.MaxQuestions <= 0 || askedCount < session.MaxQuestions)
                             && (session.MaxFollowUps <= 0 || followUpCount < session.MaxFollowUps);
            if (!budgetLeft)
                return EndOutcome("end", pendingCount);

            // (4) Quá hạn nhận bài (B2B) → không hỏi thêm (đối xứng SessionAbandonSweeper).
            // Giữ `true`: deadline kết thúc buổi THẬT, không phải chỉ hết chuỗi.
            if (session.Deadline is DateTime dl && DateTime.UtcNow > dl)
                return new AdaptiveOutcome("end", null, InterviewComplete: true);

            // (5) Lịch sử Q&A + tiêu chí (CÙNG nguồn scoring → follow-up bám cùng rubric, công bằng B2B).
            var rootQuestionId = question.RootQuestionId ?? question.Id;
            var history = perQuestionMode
                ? await BuildAdaptiveChainAsync(session.Id, rootQuestionId, question.Id, ct)
                : await BuildAdaptiveHistoryAsync(session.Id, question.Id, ct);
            var criteria = (await LoadActiveCriteriaAsync(session, ct))
                .Select(c => new DecideCriterionDto(c.Name, c.Description))
                .ToList();

            // Chế độ chuỗi: kèm câu GỐC (mỏ neo chủ đề) + tên các câu gốc KHÁC (đừng hỏi trùng).
            string? rootQuestion = null;
            List<string>? otherTopics = null;
            if (perQuestionMode)
            {
                var seeds = await _db.PracticeQuestions.AsNoTracking()
                    .Where(q => q.SessionId == session.Id && q.RootQuestionId == null)
                    .OrderBy(q => q.OrderNo)
                    .Select(q => new { q.Id, q.Content })
                    .ToListAsync(ct);
                rootQuestion = seeds.FirstOrDefault(s => s.Id == rootQuestionId)?.Content;
                otherTopics = seeds.Where(s => s.Id != rootQuestionId).Select(s => s.Content).ToList();
            }

            // (6) Transcribe đồng bộ + quyết định. Lỗi → nuốt ở catch ngoài → None (degrade tĩnh).
            var decision = await _decider!.DecideNextAsync(
                new AdaptiveDecisionRequest(
                    answer.AudioObjectKey!, session.JobCategory.ToString(), question.Content,
                    history, askedCount, followUpCount, session.MaxQuestions, session.MaxFollowUps, criteria,
                    RootQuestion: rootQuestion,
                    CurrentDepth: question.Depth,
                    MaxDepth: session.MaxDeepPerQuestion,
                    OtherTopics: otherTopics,
                    Language: session.Language),
                ct);

            // (7) Lưu transcript đồng bộ lên answer (single-source; TryPublishScoringJobAsync đọc lại → job).
            //     F11 — lưu LUÔN chỉ số cách nói đo cùng lượt transcribe đó. Đây là lần đo DUY NHẤT
            //     của câu trả lời này ở đường thích ứng: worker sau đó bỏ Whisper, nên không lưu ở
            //     đây là mất vĩnh viễn (và mất im lặng — buổi tĩnh vẫn có chỉ số, buổi này thì không).
            if (!string.IsNullOrWhiteSpace(decision.Transcript))
            {
                answer.Transcript = decision.Transcript;
                // Con dấu engine của CHÍNH bản chép vừa nhận. Gán THẲNG (kể cả null) chứ không
                // "chỉ ghi khi có": đây là bản chép mới toanh, nên AIService bản cũ không gửi dấu ⇒
                // null = "không biết engine nào" là câu trả lời trung thực. Giữ dấu cũ ở đây sẽ là
                // gán lai lịch của bản chép TRƯỚC cho bản chép SAU — con dấu nói dối, tệ hơn khuyết.
                answer.TranscriptEngine = NormalizeEngineStamp(decision.TranscriptEngine, answer.Id);
                if (decision.DeliveryMetrics is not null)
                    DeliveryMetricsMapper.Apply(answer, decision.DeliveryMetrics);
                await _db.SaveChangesAsync(ct);
            }

            // (8) Hết chuỗi → không append.
            //  - `end` / câu rỗng: AI thấy chủ đề đã khai thác xong.
            //  - `new_question` ở CHẾ ĐỘ CHUỖI cũng tính là hết chuỗi: 5 câu gốc đã phủ sẵn phạm vi, nên
            //    một câu "đổi chủ đề" mà lại nằm trong chuỗi của câu gốc này là sai ngữ nghĩa (nó sẽ được
            //    chấm như phần đào sâu của chủ đề khác). Vẫn nhận `new_question` là action HỢP LỆ trên dây
            //    để không phá hợp đồng với AIService — chỉ đổi cách xử phía server.
            // ⚠ InterviewComplete theo pendingCount, KHÔNG cứng `true`: hết chuỗi câu 1 mà còn câu 2..5
            // chưa trả lời thì buổi chưa xong.
            var endsChain = decision.Action == "end"
                            || string.IsNullOrWhiteSpace(decision.NextQuestion)
                            || (perQuestionMode && decision.Action == "new_question");
            if (endsChain)
                return EndOutcome(decision.Action, pendingCount);

            // (9) Append 1 câu kế, gắn GeneratedFromAnswerId (idempotency).
            // CHẾ ĐỘ CHUỖI: OrderNo = câu cha + 1 — chỗ này đã được `SeedOrderStride` chừa sẵn khi đánh số
            // câu gốc (1, 5, 9, …) nên luôn rảnh, và sắp theo OrderNo là ra đúng thứ tự hội thoại xen kẽ.
            // CHẾ ĐỘ CŨ: giữ nguyên "append ở đuôi" (max + 1).
            var orderNo = perQuestionMode
                ? question.OrderNo + 1
                : await _db.PracticeQuestions
                    .Where(q => q.SessionId == session.Id)
                    .MaxAsync(q => q.OrderNo, ct) + 1;
            var newQuestion = new PracticeQuestion
            {
                Id = Guid.NewGuid(),
                SessionId = session.Id,
                OrderNo = orderNo,
                Content = decision.NextQuestion!,
                // F2 — câu thích ứng theo ĐÚNG thời lượng ứng viên đã chọn cho buổi. Trước đây là hằng số
                // 120 riêng ở đây, phải "đồng bộ thủ công" với seed → chọn 4 phút mà câu AI hỏi thêm vẫn
                // 2 phút. `session` đã nằm trong scope nên đọc thẳng, không tốn query.
                TimeLimitSec = session.TimeLimitSec,
                Kind = MapKind(decision.Action),
                GeneratedFromAnswerId = answer.Id,
                // INT-17b — nối chuỗi: sâu hơn cha 1 tầng, thừa kế câu gốc của cha.
                Depth = question.Depth + 1,
                RootQuestionId = perQuestionMode ? rootQuestionId : null
            };
            _db.PracticeQuestions.Add(newQuestion);

            try
            {
                await _db.SaveChangesAsync(ct);
            }
            catch (DbUpdateException ex)
            {
                // Đua double-POST: unique index generated_from_answer_id chặn child thứ 2. Gỡ entity khỏi
                // tracker để lần SaveChanges kế (TryPublishScoringJobAsync) không cố lưu lại row hỏng.
                _db.Entry(newQuestion).State = EntityState.Detached;
                _logger.LogWarning(ex,
                    "Adaptive: append câu kế bị chặn (đua unique) cho answer {AnswerId} — bỏ qua", answer.Id);
                return AdaptiveOutcome.None;
            }

            _logger.LogInformation(
                "Adaptive: session {SessionId} answer {AnswerId} → {Action}, thêm câu {QuestionId} (order {Order})",
                session.Id, answer.Id, decision.Action, newQuestion.Id, newQuestion.OrderNo);

            return new AdaptiveOutcome(decision.Action, newQuestion, InterviewComplete: false);
        }
        catch (Exception ex)
        {
            // Degrade về luồng tĩnh: answer đã lưu, worker sẽ transcribe async như cũ. Upload KHÔNG hỏng.
            _logger.LogWarning(ex,
                "Adaptive decide-next lỗi cho answer {AnswerId} — bỏ qua (fallback luồng tĩnh)", answer.Id);

            // INT-17b — đếm lỗi để cổng (0) ngắt hẳn sau vài lần. Best-effort: đếm lỗi mà ném thì
            // thành nuốt luôn cả upload, nên bọc riêng.
            if (perQuestionMode)
            {
                try
                {
                    await _db.PracticeSessions
                        .Where(s => s.Id == session.Id)
                        .ExecuteUpdateAsync(
                            u => u.SetProperty(s => s.AdaptiveFailures, s => s.AdaptiveFailures + 1)
                                  .SetProperty(s => s.UpdatedAt, DateTime.UtcNow), ct);
                }
                catch (Exception counterEx)
                {
                    _logger.LogWarning(counterEx,
                        "Adaptive: không tăng được adaptive_failures cho session {SessionId}", session.Id);
                }
            }

            return AdaptiveOutcome.None;
        }
    }

    /// <summary>
    /// Kết quả cho mọi nhánh "không thêm câu nữa". Buổi xong ⇔ không còn câu nào chưa trả lời.
    ///
    /// ⚠ CHỈ báo action ra client khi buổi THỰC SỰ xong. Ở chế độ chuỗi, "hết chuỗi" xảy ra sau mỗi câu
    /// gốc, mà FE ánh xạ <c>end</c> thành <i>"AI đã hỏi xong — bạn có thể nộp bài."</i> ⇒ báo end lúc còn
    /// 4 câu gốc chưa trả lời là giục ứng viên nộp bài giữa chừng (mất 1 credit cho buổi làm dở). Trả
    /// action null → FE hiện "Đã nộp câu trả lời." rồi tự chuyển sang câu chưa trả lời kế tiếp.
    ///
    /// Chế độ CŨ không đổi hành vi: nhánh frontier bảo đảm <paramref name="pendingCount"/> luôn bằng 0
    /// khi tới được các điểm return này, nên vẫn ra đúng <c>(action, null, true)</c> như trước.
    /// </summary>
    private static AdaptiveOutcome EndOutcome(string? action, int pendingCount)
    {
        var complete = pendingCount == 0;
        return new AdaptiveOutcome(complete ? action : null, null, complete);
    }

    /// <summary>Trần độ dài con dấu engine. Tên model dài nhất đang biết là
    /// <c>gemini-2.5-flash-preview-native-audio-dialog</c> (43 ký tự) nên 64 là rộng gấp rưỡi. Đây là
    /// guard phát hiện RÁC (worker hỏng / lệch hợp đồng), không phải validation nghiêm ngặt.</summary>
    private const int MaxEngineStampLen = 64;

    /// <summary>
    /// Chuẩn hoá con dấu engine nhận từ AIService: rỗng/khoảng trắng và rác quá dài → <c>null</c>.
    ///
    /// <para><b>Quá dài thì bỏ hẳn chứ KHÔNG cắt cụt</b>: cắt tạo ra một tên engine chưa từng tồn tại
    /// rồi lưu nó như sự thật — đúng thứ cột này sinh ra để tránh. "Không biết" là câu trả lời trung
    /// thực (nguyên tắc BK23: con dấu sai tệ hơn con dấu khuyết).</para>
    ///
    /// <para><b>KHÔNG BAO GIỜ ném</b>: một cột kiểm toán tuyệt đối không được biến thành đường làm
    /// answer <c>Failed</c> — Failed = người luyện mất 1 credit (PAY-13). Mẫu đã có ở
    /// <c>promptVersion</c> âm (BK23) và ở F13/F11.</para>
    /// </summary>
    private string? NormalizeEngineStamp(string? raw, Guid answerId)
    {
        if (string.IsNullOrWhiteSpace(raw))
            return null;

        var trimmed = raw.Trim();
        if (trimmed.Length <= MaxEngineStampLen)
            return trimmed;

        _logger.LogWarning(
            "Bỏ con dấu engine dài bất thường ({Len} ký tự) từ AIService cho answer {AnswerId} — lưu NULL",
            trimmed.Length, answerId);
        return null;
    }

    private static QuestionKind MapKind(string action) => action switch
    {
        "follow_up" => QuestionKind.FollowUp,
        "clarify" => QuestionKind.Clarify,
        "new_question" => QuestionKind.NewQuestion,
        _ => QuestionKind.NewQuestion   // phòng hờ (end đã return trước, không tới đây)
    };

    // Lịch sử Q&A TRƯỚC câu hiện tại (currentQuestion + transcript mới nhất gửi riêng): câu theo OrderNo
    // + transcript answer tương ứng. BỎ câu hiện tại (đang hỏi câu kế của nó). Lưu ý: B2C (1 seed) mỗi
    // lượt trước đã transcribe đồng bộ → transcript đầy đủ; B2B seeds-first, các seed trước KHÔNG phải
    // frontier nên transcript có thể còn null (worker chấm async chưa xong) → prompt hiển thị "(trống)",
    // quyết định bám chủ yếu câu trả lời mới nhất (chấp nhận được — câu thích ứng B2B là phần bonus ở đuôi).
    /// <summary>
    /// INT-17b — lịch sử theo ĐÚNG CHUỖI đang đào sâu (câu gốc → … → câu cha), bỏ câu hiện tại.
    ///
    /// VÌ SAO KHÔNG GỬI CẢ BUỔI như chế độ cũ: (a) quyết định nay là "đào sâu ĐÚNG chủ đề này", lượt Q&amp;A
    /// của 4 chủ đề khác chỉ là nhiễu mời mô hình lạc đề; (b) với 5 câu gốc thì lịch sử cả buổi lên tới
    /// ~19 lượt mà phần lớn `answer` còn null (B2B chấm async chưa xong) → prompt phình bằng chữ "(trống)";
    /// (c) đây là đường ĐỒNG BỘ trong request upload, vốn đã sát timeout — chuỗi giữ mỗi lượt ≤ trần độ sâu.
    /// </summary>
    private async Task<List<DecideTurnDto>> BuildAdaptiveChainAsync(
        Guid sessionId, Guid rootQuestionId, Guid currentQuestionId, CancellationToken ct)
    {
        // Chuỗi = câu gốc (Id == root, RootQuestionId null) + mọi câu thừa kế root đó.
        var chain = await _db.PracticeQuestions.AsNoTracking()
            .Where(q => q.SessionId == sessionId
                        && q.Id != currentQuestionId
                        && (q.Id == rootQuestionId || q.RootQuestionId == rootQuestionId))
            .OrderBy(q => q.Depth).ThenBy(q => q.OrderNo)
            .Select(q => new { q.Id, q.Content, q.Kind })
            .ToListAsync(ct);

        if (chain.Count == 0) return [];

        var chainIds = chain.Select(c => c.Id).ToList();
        var transcripts = await _db.PracticeAnswers.AsNoTracking()
            .Where(a => a.SessionId == sessionId && chainIds.Contains(a.QuestionId))
            .Select(a => new { a.QuestionId, a.Transcript })
            .ToListAsync(ct);
        var byQuestion = transcripts.ToDictionary(x => x.QuestionId, x => x.Transcript);

        return chain
            .Select(q => new DecideTurnDto(
                q.Content, byQuestion.GetValueOrDefault(q.Id), q.Kind.ToString()))
            .ToList();
    }

    private async Task<List<DecideTurnDto>> BuildAdaptiveHistoryAsync(
        Guid sessionId, Guid currentQuestionId, CancellationToken ct)
    {
        var questions = await _db.PracticeQuestions.AsNoTracking()
            .Where(q => q.SessionId == sessionId && q.Id != currentQuestionId)
            .OrderBy(q => q.OrderNo)
            .Select(q => new { q.Id, q.Content, q.Kind })
            .ToListAsync(ct);

        var transcripts = await _db.PracticeAnswers.AsNoTracking()
            .Where(a => a.SessionId == sessionId)
            .Select(a => new { a.QuestionId, a.Transcript })
            .ToListAsync(ct);
        var byQuestion = transcripts.ToDictionary(x => x.QuestionId, x => x.Transcript);

        return questions
            .Select(q => new DecideTurnDto(
                q.Content, byQuestion.GetValueOrDefault(q.Id), q.Kind.ToString()))
            .ToList();
    }

    private async Task TryPublishScoringJobAsync(
        PracticeSession session, PracticeQuestion question, PracticeAnswer answer,
        CancellationToken ct)
    {
        try
        {
            // Nguồn tiêu chí tùy mode (E1): B2B chấm theo tiêu chí campaign, B2C theo rubric nghề (kèm
            // rubric_levels cho mức neo E9). Dùng chung LoadActiveCriteriaAsync với vòng adaptive để 2 đường
            // (publish chấm + quyết định câu kế) luôn khớp nguồn tiêu chí.
            var criteria = await LoadActiveCriteriaAsync(session, ct);

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
            // T7: B2C tiered sessions use their creation-time entitlement, never a later config change.
            var n = Math.Max(1, session.CampaignId is null && session.EntitlementSource != "legacy"
                ? session.SelfConsistencyN : _scoring.SelfConsistencyN);
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
                    Language = session.Language,
                    RubricVersion = rubricVersion,
                    Criteria = builtCriteria,
                    AttemptNo = attempt,
                    Temperature = attempt == 1 ? 0d : _scoring.SelfConsistencyTemperature,
                    // Phỏng vấn THÍCH ỨNG — transcript đã transcribe đồng bộ (adaptive) → worker bỏ Whisper.
                    // null (luồng tĩnh / adaptive tắt / decide lỗi) → worker tải audio + Whisper như cũ.
                    Transcript = answer.Transcript,
                    // Con dấu engine đi cùng Transcript: worker bỏ Whisper khi job có Transcript nên nó
                    // KHÔNG tự biết engine nào đã chép — không gửi kèm thì nó không có gì để echo về
                    // callback, và con dấu chỉ sống ở answer chứ không đi được vào lượt chấm.
                    TranscriptEngine = answer.TranscriptEngine,
                    // F11 — chỉ số PHẢI đi cùng Transcript: worker bỏ Whisper khi có Transcript, nên
                    // thiếu cái này là buổi thích ứng chấm "độ trôi chảy" mà không có số đo nào.
                    DeliveryMetrics = DeliveryMetricsMapper.Read(answer)
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

    // Nguồn tiêu chí active theo mode (E1/BC16) — dùng chung cho publish chấm + quyết định câu kế (adaptive):
    //   B2B: theo campaign_id. B2C: rubric RIÊNG của candidate cho nghề (nếu có) else seed mặc định (owner null).
    // Criteria materialize của campaign cũng mang JobCategory → B2C lọc thêm campaign_id IS NULL (không chấm
    // nhầm bằng tiêu chí campaign cùng nghề). E9: .Include(Levels) để có mức neo (câu mẫu jsonb trên level, DB15).
    private async Task<List<RubricCriterion>> LoadActiveCriteriaAsync(
        PracticeSession session, CancellationToken ct)
    {
        var query = _db.RubricCriteria.AsNoTracking()
            .Include(c => c.Levels)
            .Where(c => c.IsActive);
        if (session.CampaignId is Guid campaignId)
        {
            query = query.Where(c => c.CampaignId == campaignId);
        }
        else
        {
            var owner = await B2CRubricScope.ResolveOwnerAsync(_db, session.CandidateId, session.JobCategory, session.Language, ct);
            query = owner is Guid oid
                ? query.Where(c => c.CampaignId == null && c.CandidateId == oid && c.JobCategory == session.JobCategory && c.Language == session.Language)
                : query.Where(c => c.CampaignId == null && c.CandidateId == null && c.JobCategory == session.JobCategory && c.Language == session.Language);
        }
        return await query.ToListAsync(ct);
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
            var owner = await B2CRubricScope.ResolveOwnerAsync(_db, session!.CandidateId, session.JobCategory, session.Language, ct);
            critQuery = owner is Guid oid
                ? critQuery.Where(c => c.CampaignId == null && c.CandidateId == oid && c.JobCategory == session.JobCategory && c.Language == session.Language)
                : critQuery.Where(c => c.CampaignId == null && c.CandidateId == null && c.JobCategory == session.JobCategory && c.Language == session.Language);
        }
        // E8/E9: bản đồ criterionId -> tiêu chí (kèm rubric_levels) để BỎ criterion ngoài rubric,
        // KẸP [0,maxScore], và (E9) snap/lưu level_matched theo mức của tiêu chí.
        var critById = (await critQuery.ToListAsync(ct)).ToDictionary(c => c.Id);

        // E10 — attempt worker vừa chấm (echo từ job). Worker cũ không gửi → DTO default 1.
        var attemptNo = req.AttemptNo <= 0 ? 1 : req.AttemptNo;

        // BK23 — con dấu phiên bản prompt của lượt chấm này (worker chụp tại chỗ dựng prompt).
        // Chuẩn hoá ÂM → null: con dấu là tổng version các mảnh đang active, mà version có CHECK
        // `> 0` ở tầng DB, nên số âm chỉ có thể là worker hỏng/lệch hợp đồng. Lưu rác vào cột
        // kiểm toán còn tệ hơn để trống — "không biết" là câu trả lời trung thực, số bịa thì không.
        // KHÔNG ném lỗi: một cột audit không được phép biến thành đường làm answer Failed (PAY-13).
        var promptVersion = req.PromptVersion is >= 0 ? req.PromptVersion : null;
        if (req.PromptVersion is < 0)
            _logger.LogWarning(
                "Bỏ con dấu prompt_version âm ({Raw}) từ worker cho answer {AnswerId} — lưu NULL",
                req.PromptVersion, answerId);

        // Idempotency: worker retry có thể gửi lại cùng attempt+version.
        // Xoá điểm cũ cùng attempt+version rồi ghi lại, tránh nhân đôi.
        var stale = answer.Scores
            .Where(s => s.AttemptNo == attemptNo && s.RubricVersion == req.RubricVersion)
            .ToList();
        if (stale.Count > 0)
            _db.AnswerScores.RemoveRange(stale);

        // Con dấu ENGINE — phải quyết định TRƯỚC khi ghi đè `answer.Transcript` ngay dưới, vì nó
        // dựa vào việc bản chép có ĐỔI hay không.
        //
        // Ba ca, và cả ba đều thật:
        //  (a) worker gửi dấu     → ghi dấu đó (đường tĩnh: worker tự chép, tự biết engine).
        //  (b) worker KHÔNG gửi dấu nhưng transcript ĐỔI → bản chép mới mà không rõ engine ⇒ dấu về
        //      null. KHÔNG giữ dấu cũ: giữ là gán lai lịch của bản chép TRƯỚC cho bản chép SAU, tức
        //      con dấu nói dối — mà cả lý do tồn tại của cột là trả lời "hai điểm này có cùng chất
        //      lượng bản chép không". Sai thì nó trả lời SAI một cách tự tin (nguyên tắc BK23).
        //  (c) worker KHÔNG gửi dấu và transcript GIỮ NGUYÊN → worker echo lại đúng bản chép đã có
        //      trong job (nó bỏ Whisper), nên dấu cũ vẫn mô tả đúng bản chép này ⇒ GIỮ. Đây là ca
        //      thường trực của đường thích ứng khi image AIService lệch nhịp .NET.
        var incomingEngine = NormalizeEngineStamp(req.TranscriptEngine, answerId);
        var transcriptChanged = !string.Equals(answer.Transcript, req.Transcript, StringComparison.Ordinal);
        if (incomingEngine is not null || transcriptChanged)
            answer.TranscriptEngine = incomingEngine;

        answer.Transcript = req.Transcript;

        // F13 — câu trả lời mẫu (do cùng lượt chấm sinh).
        //  • attempt 1 (temperature=0, tái lập) = bản CHỌN → ghi đè, nên retry cùng attempt 1
        //    là idempotent thay vì để bản đầu tiên đóng đinh vĩnh viễn.
        //  • attempt 2..N (E10, temp>0) CHỈ điền khi còn trống → tránh nội dung hiển thị nhảy
        //    theo attempt về sau, nhưng vẫn cứu được ca attempt 1 không trả field.
        //  • rỗng/null KHÔNG xoá bản đang có: LLM im lặng bỏ field ở 1 attempt không được phép
        //    xoá gợi ý hợp lệ đã lưu.
        var incomingSample = req.SampleAnswer?.Trim();
        if (!string.IsNullOrEmpty(incomingSample)
            && (attemptNo == 1 || string.IsNullOrWhiteSpace(answer.SampleAnswer)))
        {
            answer.SampleAnswer = incomingSample;
        }

        // F11 — chỉ số cách nói (đường TĨNH: worker tự transcribe rồi tự đo). Chỉ ghi khi worker
        // thực sự gửi: null KHÔNG được xoá bản đã lưu, vì ở đường THÍCH ỨNG chỉ số đã được ghi từ
        // /decide-next và worker cũ (chưa có F11) sẽ gửi null → ghi đè null là xoá mất số đo đúng.
        // Cùng lý do idempotency của SampleAnswer: attempt 1 là bản chốt (temperature=0), 2..N chỉ
        // điền khi còn trống — nhưng chỉ số là ĐO ĐẠC nên mọi attempt đều ra cùng số, ghi đè vô hại.
        if (req.DeliveryMetrics is not null)
            DeliveryMetricsMapper.Apply(answer, req.DeliveryMetrics);

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
                // BK23 — đóng dấu thước đo lên CHÍNH dòng điểm. Đóng ở đây (mỗi dòng) chứ không
                // ở answer: một answer có N attempt (E10), mỗi attempt là một lượt gọi AI riêng
                // với lần refresh registry riêng ⇒ prompt_version là thuộc tính của ATTEMPT, không
                // phải của answer. Lưu per-row nên prompt đổi giữa chừng là thấy được, không bị
                // một giá trị "đại diện" nào đó nuốt mất.
                PromptVersion = promptVersion,
                CreatedAt = DateTime.UtcNow
            });
        }

        // Persist điểm attempt này (giữ answer.Status = Scoring cho tới khi đủ N attempt).
        await _db.SaveChangesAsync(ct);

        // E10 — self-consistency: chỉ chốt answer khi đã đủ N attempt cho rubric_version hiện tại
        //   (đếm distinct attempt_no). Chưa đủ → giữ Scoring, chờ callback attempt kế.
        //   N=1 (mặc định) → 1 attempt là đủ → hành vi cũ.
        // Session is loaded above with the answer; use the stamped B2C value for the completion gate too.
        var n = Math.Max(1, session.CampaignId is null && session.EntitlementSource != "legacy"
            ? session.SelfConsistencyN : _scoring.SelfConsistencyN);
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
            .Select(s => new { s.CriterionId, s.Score, s.Reasoning, s.PromptVersion })
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

        // BK23 — self-consistency (E10) trộn HAI THƯỚC ĐO. Điểm chốt là median giữa các attempt;
        // nếu admin sửa prompt giữa lúc N attempt của cùng answer đang chạy thì median đó được
        // lấy trên các lần chấm bằng prompt KHÁC NHAU — con số vẫn ra, vẫn trông bình thường, và
        // không có gì nói rằng nó vô nghĩa. Đó đúng là loại im lặng mà cột này sinh ra để phá.
        //
        // Cờ soi lại chứ KHÔNG loại attempt / KHÔNG Failed: bỏ attempt sẽ làm median mất mẫu (có
        // khi còn 1) và Failed thì mất credit (PAY-13) vì một thao tác của admin — người trả tiền
        // không liên quan. N=1 → 1 giá trị → không bao giờ kích hoạt (giữ nguyên hành vi cũ).
        // Chỉ so các dòng CÓ con dấu: trộn null (worker cũ) với số không chứng minh được là khác
        // thước — null nghĩa là "không biết", suy ra "khác" từ "không biết" là bịa.
        var stampedVersions = perAttempt
            .Where(s => s.PromptVersion.HasValue)
            .Select(s => s.PromptVersion!.Value)
            .Distinct()
            .ToList();
        var mixedPromptVersion = stampedVersions.Count > 1;
        if (mixedPromptVersion)
            _logger.LogWarning(
                "Answer {AnswerId}: các attempt chấm bằng prompt khác nhau ({Versions}) — "
                + "median trộn hai thước đo, gắn cờ soi lại",
                answerId, string.Join(",", stampedVersions));

        var needsReview = highSpread || shortReasoning || mixedPromptVersion;

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
