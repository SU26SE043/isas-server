using Isas.InterviewService.ApplicationDbContext;
using Isas.InterviewService.DTOs;
using Isas.InterviewService.Entities;
using Isas.InterviewService.Enums;
using Isas.InterviewService.Models;
using Isas.InterviewService.Services.Interfaces;
using Isas.Shared.Pagination;
using Isas.Shared.Validation;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Options;

namespace Isas.InterviewService.Services;

public class PracticeService : IPracticeService
{
    private const int DefaultTimeLimitSec = 120; // TODO: chỉnh nếu Gemini trả kèm

    private const string GenerationFailedReason = "generation_failed"; // BK12

    private readonly InterviewDbContext _db;
    private readonly IStorageService _storage;
    private readonly IAiServiceQuestionGenerator _questionGenerator;
    private readonly ISessionScoringNotifier _scoringNotifier;
    private readonly ICreditReservationClient _reservationClient;   // BC2
    private readonly IEntitlementClient? _entitlements;
    private readonly bool _tieringEnabled;
    private readonly AdaptiveOptions _adaptive;   // phỏng vấn THÍCH ỨNG (B2C seed count + toggle)
    private readonly ICriterionBenchmarkService? _benchmarks;   // F14 — mốc đối chiếu radar (null = tắt)
    private readonly IKnowledgeService? _knowledge;   // RAG grounding — retrieval (null = tắt, giữ luồng cũ)
    private readonly GroundingOptions _grounding;     // RAG grounding — Enabled/TopK/threshold
    private readonly ILogger<PracticeService> _logger;
    private readonly bool _consumeAtGeneration;   // PONR1 — kill-switch Billing:ConsumeAtQuestionGeneration
    private readonly bool _bilingualEnabled;
    private readonly CapacityOptions _capacity;

    public PracticeService(
        InterviewDbContext db,
        IStorageService storage,
        IAiServiceQuestionGenerator questionGenerator,
        ISessionScoringNotifier scoringNotifier,
        ICreditReservationClient reservationClient,
        ILogger<PracticeService> logger,
        // Optional (default null) → mọi test dựng PracticeService cũ (6 tham số) vẫn compile + adaptive tắt;
        // DI inject bản thật (Configure<AdaptiveOptions>). null → AdaptiveOptions mặc định (Enabled=false).
        IOptions<AdaptiveOptions>? adaptiveOptions = null,
        // F14 — optional cùng lý do: test cũ dựng service không truyền → không có benchmark, kết quả
        // giữ nguyên hình dạng trước F14 (Benchmark=null) thay vì vỡ.
        ICriterionBenchmarkService? benchmarks = null,
        IConfiguration? config = null,
        // RAG grounding — optional (default null/tắt): test cũ không truyền → không đi đường grounding
        // (câu hỏi GroundingRefs=null, hành vi trước grounding y nguyên). DI inject bản thật khi Grounding:Enabled.
        IKnowledgeService? knowledge = null,
        IOptions<GroundingOptions>? groundingOptions = null,
        IEntitlementClient? entitlements = null,
        IOptions<CapacityOptions>? capacityOptions = null)
    {
        _db = db;
        _storage = storage;
        _questionGenerator = questionGenerator;
        _scoringNotifier = scoringNotifier;
        _reservationClient = reservationClient;
        _adaptive = adaptiveOptions?.Value ?? new AdaptiveOptions();
        _benchmarks = benchmarks;
        _knowledge = knowledge;
        _grounding = groundingOptions?.Value ?? new GroundingOptions();
        _entitlements = entitlements;
        _tieringEnabled = bool.TryParse(config?["Tiering:Enabled"], out var tiering) && tiering;
        _bilingualEnabled = bool.TryParse(config?["Interview:Bilingual:Enabled"], out var bilingual) && bilingual;
        _logger = logger;
        // PONR1/PONR3 — thu ở mốc Ready là thay đổi chính sách tiền. Phải opt-in tường minh;
        // thiếu/sai config = luật cũ (consume khi Scored), để không thể bật thu tiền trước khi UI
        // PONR3 đã thông báo cho người dùng.
        _consumeAtGeneration = bool.TryParse(
            config?["Billing:ConsumeAtQuestionGeneration"], out var consumeAtGeneration)
            && consumeAtGeneration;
        _capacity = capacityOptions?.Value ?? new CapacityOptions();
    }

    // ── CREATE: tạo session + sinh câu hỏi (1 call) ───────────────────────
    public Task<PracticeSessionResponse> CreateSessionAsync(
        Guid candidateId, CreatePracticeSessionRequest request, CancellationToken ct = default)
        => CreateSessionInternalAsync(candidateId, request, Guid.NewGuid(), focusCriteria: null, ct);

    // BC14 — /start roadmap lesson: sessionId do caller cấp (link lesson sau khi tạo → thoả FK
    // roadmap_lessons.session_id) + câu hỏi bám focusCriteria của milestone. Reserve/gen/BK12 giữ nguyên.
    public Task<PracticeSessionResponse> CreateLessonSessionAsync(
        Guid candidateId, CreatePracticeSessionRequest request, Guid sessionId,
        IReadOnlyList<string>? focusCriteria, CancellationToken ct = default)
        => CreateSessionInternalAsync(candidateId, request, sessionId, focusCriteria, ct);

    /// <summary>
    /// SC3 — preview số câu/số câu gốc do SERVER tính bằng ĐÚNG luật tạo session.
    ///
    /// <para><paramref name="language"/> đi qua <see cref="ValidateLanguage"/> — CÙNG hàm mà đường tạo
    /// session dùng. Trước đây chốt cứng <c>"vi"</c> với lý do "request tạo session cũng mặc định vi",
    /// nhưng <c>CreatePracticeSessionRequest</c> CÓ field <c>Language</c> và song ngữ đã bật thật trên
    /// prod ⇒ buổi <c>en</c> preview bằng rubric <c>vi</c>. Không phải sai số hiển thị: số tiêu chí nội
    /// dung là SÀN của số câu gốc (<see cref="ComputeSeedCount"/>, thắng cả trần config) nên preview và
    /// buổi thật ra hai con số khác nhau.</para>
    /// </summary>
    public async Task<PracticeSessionOptionsResponse> GetSessionOptionsAsync(
        Guid candidateId, string jobCategory, string? language = null, CancellationToken ct = default)
    {
        if (!Enum.TryParse<JobCategory>(jobCategory, ignoreCase: true, out var category))
            throw new InvalidOperationException("jobCategory không hợp lệ.");
        var resolvedLanguage = ValidateLanguage(language);

        var entitlement = _tieringEnabled && _entitlements is not null
            ? await _entitlements.ResolveUserAsync(candidateId, ct)
            : EntitlementSnapshot.Free;
        var criteriaCount =
            (await LoadTargetableCriteriaAsync(candidateId, category, resolvedLanguage, ct)).Count;
        var baseline = ResolveSessionSettings(null, entitlement);
        var max = EffectiveMaxQuestionCount(entitlement);
        var min = MinQuestionCount;

        PracticeSessionPreview PreviewFor(int count)
        {
            var settings = ResolveSessionSettings(count, entitlement);
            var seeds = settings.MaxDeepPerQuestion > 0
                ? ComputeSeedCount(settings.MaxQuestions, settings.MaxDeepPerQuestion, _adaptive.SeedCount, criteriaCount)
                : count;
            return new PracticeSessionPreview(count, seeds);
        }

        var preview = Enumerable.Range(min, max - min + 1).Select(PreviewFor).ToList();

        // `CoversAllCriteria` = "có ĐỦ KHE cho mọi tiêu chí nội dung", tính GIỐNG NHAU ở cả hai chế độ.
        //
        // Trước đây `!baseline.AdaptiveEnabled ||` cho ra `true` VÔ ĐIỀU KIỆN khi adaptive tắt ⇒ UI
        // khẳng định "phủ hết tiêu chí" kể cả preset 1 câu. Adaptive tắt KHÔNG làm câu hỏi hết nhãn:
        // `targetable` vẫn được gửi vào `GenerateQuestionsAsync` (xem CreateSessionInternalAsync) và
        // `PreviewFor` trả `seeds = count` cho nhánh đó, nên phép so vẫn có nghĩa và vẫn đúng đại lượng.
        //
        // ⚠ Đây là điều kiện CẦN, không phải đủ: AI vẫn có thể gắn trùng nhãn (đo trên prod: 3 câu gốc
        // nhưng nhãn ra 2 lần cùng một tiêu chí — xem log cảnh báo SC1 ở call site tạo session).
        PracticeSessionPreset Preset(string key, int desired)
        {
            var item = PreviewFor(Math.Clamp(desired, min, max));
            return new PracticeSessionPreset(
                key, item.QuestionCount, item.SeedCount, item.SeedCount >= criteriaCount);
        }

        // Preset SẬP TRÙNG NHAU khi trần thấp: gói cap 8 → short=6, medium=8, long=8 ⇒ UI hiện 3 nút mà
        // 2 nút y hệt. Dedupe theo `QuestionCount`, giữ nhãn CUỐI cùng để giá trị lớn nhất còn dùng được
        // giữ nhãn "long" (giữ nhãn đầu thì nút to nhất lại mang tên "medium").
        var presets = new[] { Preset("short", 6), Preset("medium", 12), Preset("long", 20) }
            .GroupBy(p => p.QuestionCount)
            .Select(g => g.Last())
            .OrderBy(p => p.QuestionCount)
            .ToList();

        // Mặc định THẬT khi client bỏ trống `questionCount`, KHÔNG phải một con số UI tự đặt.
        // `ResolveSessionSettings(null, …)` chạy đúng nhánh mà `CreateSessionInternalAsync` sẽ chạy
        // (`questionCount ?? entitlement.MaxQuestions` / `?? _adaptive.MaxQuestions`). Trần buổi 0 =
        // "không trần" (adaptive tắt) ⇒ số câu do AIService quyết, mặc định của nó là 5.
        var defaultQuestionCount = Math.Clamp(
            baseline.MaxQuestions > 0 ? baseline.MaxQuestions : AiServiceDefaultQuestionCount, min, max);

        // Dải độ sâu — dựng bằng ĐÚNG hàm mà đường tạo buổi dùng để từ chối, nên hai bên không thể
        // lệch nhau. `max == 0` (adaptive tắt) ⇒ min cũng 0 ⇒ UI ẩn ô chọn thay vì hiện một ô vô hiệu.
        var selectableMaxDeep = SelectableMaxDeepPerQuestion(entitlement);
        var selectableMinDeep = selectableMaxDeep > 0 ? 1 : 0;

        return new PracticeSessionOptionsResponse(
            baseline.AdaptiveEnabled, baseline.MaxDeepPerQuestion, criteriaCount, min, max,
            defaultQuestionCount, presets, preview, selectableMinDeep, selectableMaxDeep);
    }

    // Lõi dùng chung cho CreateSessionAsync (sessionId ngẫu nhiên, không focus) và
    // CreateLessonSessionAsync (sessionId caller cấp + focusCriteria roadmap lesson).
    private async Task<PracticeSessionResponse> CreateSessionInternalAsync(
        Guid candidateId, CreatePracticeSessionRequest request, Guid sessionId,
        IReadOnlyList<string>? focusCriteria, CancellationToken ct)
    {
        // jobCategory BẮT BUỘC. Guard NGAY ĐẦU (trước cả đọc CV/reserve) → thiếu → 400 (controller map
        // InvalidOperationException → BadRequest), KHÔNG giữ credit oan (PAY-5). HTTP thật cũng 400 sớm
        // hơn nhờ [Required]; test gọi service trực tiếp nên cần guard này (mẫu CvAnalysisService/BK6).
        if (request.JobCategory is null)
            throw new InvalidOperationException("jobCategory là bắt buộc.");
        var jobCategory = request.JobCategory.Value;
        var seniority = ValidateSeniority(request.Seniority);
        var language = ValidateLanguage(request.Language);

        // F2 — thời lượng mỗi câu. Guard TRƯỚC reserve (PAY-5): giá trị sai → 400 mà KHÔNG giữ credit oan.
        var timeLimitSec = ValidateTimeLimitSec(request.TimeLimitSec);

        // F2b — số câu. Cùng lý do đặt trước reserve: 21 câu phải bị từ chối mà không trừ credit.
        var questionCount = ValidateQuestionCount(request.QuestionCount);

        // Độ sâu ứng viên chọn. Trần tuyệt đối kiểm ở đây (rẻ, chưa cần entitlement); trần THẬT theo
        // cấu hình kiểm ngay sau khi resolve entitlement — cả hai vẫn nằm TRƯỚC reserve.
        var maxDeepChoice = ValidateMaxDeepPerQuestion(request.MaxDeepPerQuestion);
        // Vietnamese seed existed before the bilingual rollout; the guard is needed for the new
        // English path, whose seed can be absent if its migration has not yet been applied.
        if (language == "en")
            await EnsureRubricExistsAsync(candidateId, jobCategory, language, ct);
        var entitlement = _tieringEnabled && _entitlements is not null
            ? await _entitlements.ResolveUserAsync(candidateId, ct)
            : EntitlementSnapshot.Free;

        // F2b/SC3 — trần số câu THẬT phụ thuộc GÓI, nên chỉ kiểm được sau khi resolve entitlement.
        // Vẫn TRƯỚC reserve (PAY-5): vượt trần → 400 mà KHÔNG giữ credit oan.
        //
        // Thiếu guard này thì `Math.Clamp` trong `ResolveSessionSettings` cắt trong IM LẶNG: gói cap 8,
        // ứng viên chọn 20 → `ValidateQuestionCount` (1..20) cho qua → session ra 8 câu. Không lỗi,
        // không cảnh báo, và `GET /session-options` thì đã báo max = 8 ⇒ API tự mâu thuẫn với chính nó.
        // Clamp bên trong GIỮ NGUYÊN làm lưới an toàn cho đường internal không đi qua guard này.
        var effectiveMaxQuestions = EffectiveMaxQuestionCount(entitlement);
        if (questionCount is int chosenQuestionCount && chosenQuestionCount > effectiveMaxQuestions)
            throw new InvalidOperationException(
                $"questionCount phải nằm trong khoảng {MinQuestionCount}..{effectiveMaxQuestions} "
                + $"(đang gửi: {chosenQuestionCount}).");

        // Trần độ sâu THẬT. Bất đối xứng có chủ đích ở nhánh `selectableMaxDeep == 0`:
        //   • cap >= 1 mà ứng viên chọn cao hơn ⇒ NÉM 400 (họ gõ một con số sai, phải biết).
        //   • cap == 0 ⇒ KẸP LẶNG + cảnh báo log, KHÔNG ném. `0` không phải "người dùng sai" mà là
        //     TRẠNG THÁI VẬN HÀNH (adaptive tắt / kill-switch). Kéo cần gạt lúc sự cố mà làm mọi tab
        //     FE đang mở — options cache của chúng vẫn báo max = 3 — nhận 400 khi bấm "Bắt đầu" thì
        //     cần gạt giảm tải tự nó thành sự cố. Câu lỗi cũng vô nghĩa: "phải trong khoảng 1..0".
        //     FE chỉ gửi trường này khi `maxDeepPerQuestionMax > 0`, nên nhánh này gần như chỉ còn
        //     phục vụ đúng cửa sổ giữa lúc gạt cần và lúc tab được tải lại.
        var selectableMaxDeep = SelectableMaxDeepPerQuestion(entitlement);
        if (maxDeepChoice is int chosenDeep && selectableMaxDeep > 0 && chosenDeep > selectableMaxDeep)
            throw new InvalidOperationException(
                $"maxDeepPerQuestion phải nằm trong khoảng 1..{selectableMaxDeep} (đang gửi: {chosenDeep}).");
        if (maxDeepChoice is not null && selectableMaxDeep == 0)
            _logger.LogWarning(
                "Buổi {SessionId}: ứng viên chọn maxDeepPerQuestion={Chosen} nhưng đào sâu đang tắt — bỏ qua lựa chọn.",
                sessionId, maxDeepChoice.Value);

        // JD nhập tay: chuẩn hoá + cap độ dài NGAY ĐẦU, TRƯỚC cả đọc CV và reserve — guard rẻ nhất
        // (thuần in-memory) chạy trước → JD quá dài → 400 mà không tốn round-trip storage và KHÔNG giữ
        // credit oan (mẫu BK6/PAY-5). Text rỗng/toàn khoảng trắng = coi như KHÔNG nhập (rơi về jdId).
        var jdTextInput = NormalizeText(request.JdText);

        // CV optional: chỉ parse khi có. Không có CV cũng luyện được (dựa JobCategory).
        // TODO: xác nhận tên method storage (memory ghi GetParseText).
        string? cvText = null;
        if (request.CvId is not null)
        {
            // Owner-scoped: file của người khác coi như không tồn tại (interview.md §Validation).
            cvText = await _storage.GetOwnedParsedTextAsync(request.CvId.Value, candidateId, ct);
            if (string.IsNullOrWhiteSpace(cvText))
                throw new InvalidOperationException("CV không đọc được nội dung");
        }

        // JD optional, 2 nguồn: text nhập thẳng (jdText) HOẶC file đã upload (jdId).
        // TEXT ƯU TIÊN FILE — quy ước C11 đã chốt bên B2B/Campaign, áp nguyên sang B2C cho nhất quán:
        // gửi cả hai thì text thắng và file bị bỏ hẳn (không parse, không lưu jd_id) → row không "nhận vơ"
        // một file thực ra không góp gì vào câu hỏi. (jdTextInput đã chuẩn hoá + kiểm ngưỡng ở đầu hàm.)
        var jdIdToUse = jdTextInput is not null ? null : request.JdId;

        string? jdText = jdTextInput;
        if (jdTextInput is null && request.JdId is not null)
        {
            jdText = await _storage.GetOwnedParsedTextAsync(request.JdId.Value, candidateId, ct);
            if (string.IsNullOrWhiteSpace(jdText))
                throw new InvalidOperationException("JD không đọc được nội dung");
        }

        await EnsureCapacityAsync(ct); // phải trước reserve: đầy không được giữ credit oan.

        // BC2: reserve 1 credit ví cá nhân (owner=User) TRƯỚC khi tạo session row.
        // sessionId cấp trước → reserve khoá idempotency theo đúng Id session sẽ dùng (P4).
        // Ví hết credit → Payment 402 → InsufficientCreditException ném ở đây ⇒ KHÔNG có row session (PAY-5).
        // (AI sinh câu hỏi lỗi SAU reserve → session Failed nhưng credit đã giữ; BC4 release khi Abandoned/Failed.)
        var reservation = await _reservationClient.ReserveAsync(
            ownerType: "User", ownerId: candidateId, sessionId: sessionId, ct: ct);
        _logger.LogInformation(
            "Reserve credit ví cá nhân cho session {SessionId} (candidate {CandidateId}, reservation {ReservationId})",
            sessionId, candidateId, reservation.ReservationId);

        // P1-2 — TỪ ĐÂY reserve ĐÃ THÀNH CÔNG (credit đã trừ). Nếu BẤT KỲ bước sau ném (SaveChanges,
        // AI gen, lưu câu hỏi…) mà không hoàn chỗ giữ → reservation treo → credit MẤT. Bọc toàn bộ
        // hậu-reserve trong try/catch: mọi lỗi → ReleaseAsync(sessionId) best-effort (idempotent PAY-11,
        // an toàn cả khi nhánh gen-fail đã phát SessionAbandoned) TRƯỚC khi ném lại. Không đổi happy path.
        try
        {
            // INT-17b — chế độ chuỗi đào sâu. Nguồn bật/tắt adaptive nay có thể là ENTITLEMENT (T7) chứ
            // không chỉ `Adaptive:Enabled`, nên bám theo đúng cờ đã resolve — đọc lại `_adaptive.Enabled`
            // ở đây sẽ khiến buổi bật adaptive bằng gói dịch vụ lại chạy chế độ cũ mà không lỗi gì.
            var settings = ResolveSessionSettings(
                questionCount, entitlement, maxDeepChoice, request.AdaptiveEnabled);
            var adaptiveOn = settings.AdaptiveEnabled;
            var maxDeepPerQuestion = settings.MaxDeepPerQuestion;

            // Con dấu thước đo B2C — đóng NGAY LÚC TẠO, trước khi có answer nào. Từ đây mọi đường
            // (publish · callback guard · republisher · tổng kết BC9) đọc cùng một bộ, kể cả khi admin
            // hoặc chính ứng viên lưu bản rubric mới ngay giữa buổi.
            var (b2cRubricOwnerId, b2cRubricVersion) =
                await ResolveB2CRubricPinAsync(candidateId, jobCategory, language, ct);

            // Tạo session, commit #1. Status set bằng C# initializer của entity.
            var session = new PracticeSession
            {
                Id = sessionId,
                CandidateId = candidateId,
                CvId = request.CvId,           // có thể null
                JdId = jdIdToUse,              // null khi JD đến từ text (C11: text ưu tiên file)
                JobCategory = jobCategory,
                Seniority = seniority,
                Language = language,
                Status = SessionStatus.GeneratingQuestions,
                CreatedAt = DateTime.UtcNow,
                B2CRubricOwnerId = b2cRubricOwnerId,
                B2CRubricVersion = b2cRubricVersion,
                TimeLimitSec = timeLimitSec,   // F2 — đóng dấu lựa chọn để câu THÍCH ỨNG sinh sau đọc lại
                // Phỏng vấn THÍCH ỨNG (B2C): đóng dấu toggle/trần từ cấu hình. Tắt → luồng batch tĩnh cũ.
                AdaptiveEnabled = adaptiveOn,
                // F2b — adaptive BẬT: trần tổng số câu lấy theo lựa chọn của ứng viên (không chọn →
                // cấu hình). Adaptive TẮT: 0 = không trần (số câu do AIService sinh 1 lần, đã cap ở
                // questionCount rồi). CHECK ở DB chặn 0..20 cho mọi đường ghi.
                MaxQuestions = settings.MaxQuestions,
                // INT-17b — ở chế độ chuỗi, trần theo BUỔI phải để 0: để nguyên 3 thì nó bó chặt hơn trần
                // theo CÂU (5 gốc × 3 = 15 câu sâu) và hội thoại chết ở câu đào sâu thứ 3. `MaxQuestions`
                // mới là trần buổi. Áp cho CẢ nguồn entitlement (T7) lẫn nguồn config.
                MaxFollowUps = settings.MaxFollowUps,
                // INT-17b — trần đào sâu MỖI câu gốc. 0 = chế độ frontier cũ (AnswerService rẽ nhánh theo
                // đúng field này) ⇒ tắt adaptive cũng cho 0, hành vi y như trước.
                MaxDeepPerQuestion = maxDeepPerQuestion,
                EntitlementSource = _tieringEnabled ? entitlement.Source : "legacy",
                TierCode = _tieringEnabled ? entitlement.TierCode : "free",
                TierRank = _tieringEnabled ? entitlement.TierRank : 0,
                GroundingEnabled = _tieringEnabled ? entitlement.GroundingEnabled : _grounding.Enabled,
                SelfConsistencyN = _tieringEnabled ? entitlement.SelfConsistencyN : 1,
                CvAnalysisIncluded = _tieringEnabled && entitlement.CvAnalysisIncluded,
                RepoAnalysisIncluded = _tieringEnabled && entitlement.RepoAnalysisIncluded,
                RoadmapEnabled = _tieringEnabled && entitlement.RoadmapEnabled
            };
            _db.PracticeSessions.Add(session);
            await _db.SaveChangesAsync(ct);

            // Tiêu chí NỘI DUNG của rubric buổi này (ScoringScope.WhenTargeted) — cấp cho AIService để
            // nó gắn nhãn "câu i nhắm tiêu chí nào". KHÔNG gửi 4 tiêu chí CÁCH NÓI: chúng được chấm cho
            // mọi câu nên đưa vào chỉ mời mô hình gắn nhãn thừa.
            // Rỗng (rubric riêng BC16 chưa phân loại / seed chưa apply) → không có gì để gắn nhãn → giữ
            // NGUYÊN đường gọi cũ bên dưới, câu hỏi không nhãn → chấm đủ rubric như trước.
            //
            // ⚠ Nạp TRƯỚC khi tính số câu gốc (SC1): chính con số này là SÀN của số câu gốc — xem
            // `ComputeSeedCount`. Trước SC1 nó nằm sau chỗ tính `seedCount`, nên số câu gốc hoàn toàn
            // không biết rubric có mấy tiêu chí nội dung.
            var targetable = await LoadTargetableCriteriaAsync(
                candidateId, jobCategory, language, ct);

            // Evidence-driven adaptive: state khởi tạo cùng snapshot rubric của buổi. AIService chỉ
            // nhận state qua wire; InterviewService là nơi duy nhất ghi state/evidence (GEN-4).
            //
            // ⚠ GATE theo `adaptiveOn` — đối xứng đường B2B (`if (session.AdaptiveEnabled)`). Evidence chỉ
            // được ĐỌC và GHI trong `AnswerService` ở đường thích ứng; buổi tắt adaptive mà vẫn khởi tạo
            // thì N row đứng `UNKNOWN` vĩnh viễn — và `GetSessionAsync` TRẢ CHÚNG RA API
            // (`CriterionEvidence`), nên FE hiện "chưa có bằng chứng cho mọi tiêu chí" cho một buổi mà cơ
            // chế đó không hề chạy.
            //
            // ⚠ Tập criterion khác nhau giữa hai đường là CÓ LÝ DO, không phải lệch: cả hai đều nghĩa là
            // "tiêu chí NỘI DUNG của rubric buổi này", nhưng B2B không dùng `ScoringScope` (tiêu chí do HR
            // gõ, nhận DEFAULT `Always` — xem SC2) nên lọc `WhenTargeted` bên đó sẽ ra RỖNG và giết luôn
            // evidence của B2B. B2C thì phải lọc, vì 4 tiêu chí CÁCH NÓI được chấm cho mọi câu ⇒ không có
            // "câu nhắm tới" để mà theo dõi bằng chứng.
            if (adaptiveOn && targetable.Count > 0)
            {
                _db.SessionCriterionEvidence.AddRange(targetable.Select(c => new SessionCriterionEvidence
                {
                    SessionId = session.Id,
                    CriterionId = c.CriterionId,
                    CriterionName = c.Name,
                    State = "UNKNOWN"
                }));
                await _db.SaveChangesAsync(ct);
            }

            // INT-17b — số câu GỐC của buổi.
            //
            // ⚠ `questionCount` VẪN là "tổng số câu của buổi" (F2b), KHÔNG phải số câu gốc. Đừng đổi thành
            // `seeds × (1 + đào sâu)`: `ValidateQuestionCount` nhận 1..20 nên chọn 6 sẽ ra trần 24, vi phạm
            // CHECK `ck_practice_sessions_max_questions_range` NGAY LÚC INSERT — tức là SAU `ReserveAsync`
            // ⇒ đúng lỗi PAY-5 (mất tiền + reservation mồ côi) mà `ClampCampaignMaxQuestions` sinh ra để
            // chặn, lần này trên đường B2C. Nó cũng âm thầm đổi thứ ứng viên đã mua bằng 1 credit.
            //
            // ⚠ Rẽ theo TRẦN ĐỘ SÂU, KHÔNG theo `adaptiveOn` — đây mới là kill-switch thật. Rẽ theo
            // `adaptiveOn` thì đặt `MaxDeepPerQuestion = 0` vẫn đổi số câu xin AI **và** đổi overload
            // được gọi ở dưới ⇒ "tắt" mà hành vi không quay lại như trước INT-17b.
            int? seedCount = maxDeepPerQuestion > 0
                ? ComputeSeedCount(
                    session.MaxQuestions, maxDeepPerQuestion, _adaptive.SeedCount, targetable.Count)
                : null;

            // SC1 — ngân sách buổi không đủ chỗ cho mọi tiêu chí nội dung. Tiêu chí không được câu nào
            // hỏi sẽ bị LOẠI khỏi điểm (đúng thiết kế chấm-theo-phạm-vi) ⇒ buổi này đo bằng ít tiêu chí
            // hơn buổi khác. Không ném (ứng viên đã trả credit, và đây là hệ quả của lựa chọn số câu),
            // nhưng phải nói ra chứ không cắt im lặng (tiền lệ F9).
            if (seedCount is int planned && planned < targetable.Count)
                _logger.LogWarning(
                    "SC1: buổi {SessionId} chỉ có {Seeds} câu gốc cho {Criteria} tiêu chí nội dung "
                    + "(trần buổi {MaxQuestions}, trần đào sâu {MaxDeep}) — {Missing} tiêu chí sẽ không "
                    + "được hỏi và bị loại khỏi điểm",
                    session.Id, planned, targetable.Count, session.MaxQuestions, maxDeepPerQuestion,
                    targetable.Count - planned);

            // Chế độ chuỗi → xin AI ĐÚNG số câu gốc (trước đây xin `questionCount` rồi vứt bớt ở bước
            // `Take` bên dưới = trả tiền token cho câu không bao giờ dùng). Trần độ sâu 0 → giữ nguyên
            // `questionCount` như trước INT-17b.
            var requestedCount = seedCount ?? questionCount;

            // Gọi Gemini NGOÀI transaction — không giữ DB connection lúc chờ AI.
            // Prompt tự xử 3 kịch bản: có JD ưu tiên JD; chỉ CV thì bám CV; không có
            // gì thì sinh câu hỏi chung theo JobCategory. focusCriteria (lesson /start) đưa thêm để bám tiêu chí.
            //
            // RAG grounding — khi Grounding:Enabled + có KnowledgeService: RETRIEVE chunk uy tín theo jobCategory
            // (degrade rỗng khi lỗi/miss) → truyền vào overload grounded → nhận citations per-câu. Tắt → luồng cũ.
            List<GeneratedQuestion> generated;
            IReadOnlyList<GroundingChunk> grounding = Array.Empty<GroundingChunk>();
            IReadOnlyList<QuestionCitationDto> citations = Array.Empty<QuestionCitationDto>();
            // Rows created before T7 have source=legacy and must keep the old global rollout behaviour.
            var grounded = (session.EntitlementSource == "legacy" ? _grounding.Enabled : session.GroundingEnabled)
                && _knowledge is not null;

            try
            {
                if (targetable.Count > 0)
                {
                    if (grounded)
                        grounding = await _knowledge!.RetrieveAsync(
                            session.JobCategory.ToString(),
                            BuildRetrievalQuery(session.JobCategory.ToString(), cvText, jdText, focusCriteria), ct);

                    var result = await _questionGenerator.GenerateQuestionsAsync(
                        session.JobCategory.ToString(), cvText, jdText, focusCriteria, requestedCount,
                        grounded ? grounding : null, session.Language, targetable, session.Seniority, ct);
                    generated = result.Questions;
                    citations = result.Citations;
                }
                else if (grounded)
                {
                    // RetrieveAsync tự nuốt mọi lỗi → rỗng (degrade). grounding rỗng vẫn gọi overload grounded
                    // để LUÔN emit citations (ít nhất []) — phân biệt "đã grounding, miss" với "không grounding".
                    grounding = await _knowledge!.RetrieveAsync(
                        session.JobCategory.ToString(),
                        BuildRetrievalQuery(session.JobCategory.ToString(), cvText, jdText, focusCriteria), ct);
                    // SEN1 — bỏ nhánh rẽ `Language == "vi"` ở ĐÂY (chỉ ở đây): overload `grounding+ct`
                    // không mang được `seniority` (đụng độ chữ ký, xem interface), mà nhánh đó chạy
                    // đúng khi `session.Language` LÀ "vi" ⇒ gọi thẳng overload `language` với
                    // `session.Language` cho ra payload y hệt, chỉ thêm `seniority`.
                    var result = await _questionGenerator.GenerateQuestionsAsync(
                        session.JobCategory.ToString(), cvText, jdText, focusCriteria, requestedCount,
                        grounding, session.Language, session.Seniority, ct);
                    generated = result.Questions;
                    citations = result.Citations;
                }
                // Dùng overload ĐẦY ĐỦ khi có focusCriteria (BC14) HOẶC đã biết số câu cần xin (F2b /
                // INT-17b); còn lại giữ nguyên overload 4 tham số của luồng thường (không đổi hợp đồng
                // mock cũ — adaptive TẮT + không chọn số câu vẫn phải rơi vào đúng nhánh này).
                else if (focusCriteria is { Count: > 0 } || requestedCount is not null)
                    generated = session.Language == "vi"
                        ? await _questionGenerator.GenerateQuestionsAsync(session.JobCategory.ToString(), cvText, jdText, focusCriteria, requestedCount, session.Seniority, ct)
                        : (await _questionGenerator.GenerateQuestionsAsync(session.JobCategory.ToString(), cvText, jdText, focusCriteria, requestedCount, null, session.Language, session.Seniority, ct)).Questions;
                else
                    generated = session.Language == "vi"
                        ? await _questionGenerator.GenerateQuestionsAsync(session.JobCategory.ToString(), cvText, jdText, session.Seniority, ct)
                        : (await _questionGenerator.GenerateQuestionsAsync(session.JobCategory.ToString(), cvText, jdText, null, null, null, session.Language, session.Seniority, ct)).Questions;
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Sinh câu hỏi lỗi cho session {SessionId}", session.Id);
                session.Status = SessionStatus.Failed;
                await EnqueueGenerationFailedAbandonAsync(session, ct);   // BK12: outbox release credit (atomic Failed)
                // AI upstream lỗi (AiServiceException = transport/timeout/non-2xx) → propagate NGUYÊN
                // TYPE để controller map 502 (không bọc thành InvalidOperationException = 400, che lỗi
                // thật). Reserve vẫn được release ở catch ngoài (P1-2) + abandon (BK12) — idempotent PAY-11.
                // Lỗi khác (generic) giữ 400 như cũ.
                if (ex is AiServiceException) throw;
                throw new InvalidOperationException("Sinh câu hỏi thất bại", ex);
            }

            if (generated is null || generated.Count == 0)
            {
                session.Status = SessionStatus.Failed;
                await EnqueueGenerationFailedAbandonAsync(session, ct);   // BK12: outbox release credit (atomic Failed)
                throw new InvalidOperationException("AIService không trả về câu hỏi nào");
            }

            // Phỏng vấn THÍCH ỨNG (B2C): bật → giữ SeedCount câu đầu làm SEED (phần còn lại AI sinh động
            // theo câu trả lời trong AnswerService). Tắt → giữ CẢ bộ như luồng cũ. Kind=Seed (mặc định entity).
            // Chốt phòng thủ: chế độ chuỗi đã xin AI đúng `seedCount` nên `Take` thường là no-op, nhưng AI
            // vẫn có thể trả THỪA — không cắt thì buổi có nhiều câu gốc hơn ngân sách đã đóng dấu.
            // Nhánh giữa = kill-switch (trần độ sâu 0) + adaptive bật: cắt theo `SeedCount` ĐÚNG như trước
            // INT-17b, để đặt trần 0 là quay lại nguyên hành vi cũ chứ không phải một hình dạng thứ ba.
            var seedQuestions = seedCount is int sc
                ? generated.Take(sc).ToList()
                : adaptiveOn
                    ? generated.Take(Math.Max(1, _adaptive.SeedCount)).ToList()
                    : generated;

            // SC1 — kiểm PHỦ sau khi AI gắn nhãn: tiêu chí nội dung nào không câu nào nhắm tới?
            //
            // Prompt đã yêu cầu trải đều, nhưng đó là lời dặn model chứ không phải bất biến — đo trên
            // prod: 3 câu gốc, 2 câu cùng một tiêu chí, tiêu chí thứ ba không bao giờ được hỏi.
            //
            // ⚠ CỐ Ý CHỈ LOG, không sửa nhãn. Hai cách "chữa" đều tệ hơn:
            //   • gán bù tiêu chí thiếu vào câu chưa có nhãn = BỊA — ứng viên bị chấm đúng thứ họ không
            //     hề được hỏi, chính là lỗi mà chấm-theo-phạm-vi sinh ra để diệt;
            //   • bỏ sạch nhãn để quay về chấm cả rubric = lùi về nguyên hành vi trước chấm-theo-phạm-vi,
            //     tức áp lỗi đó cho MỌI câu thay vì một tiêu chí.
            // Thiếu phủ làm buổi này đo bằng ít tiêu chí hơn buổi khác — đắt, nhưng vẫn rẻ hơn cả hai.
            // Ném thì càng không: buổi đã reserve credit (PAY-5), biến một cái nhãn thành đường làm
            // hỏng cả buổi là đánh đổi sai (mẫu fail-open của `fullName` BK28).
            if (targetable.Count > 0)
            {
                var covered = seedQuestions
                    .Where(q => q.TargetCriterionIds is not null)
                    .SelectMany(q => q.TargetCriterionIds!)
                    .ToHashSet();
                var missing = targetable.Where(c => !covered.Contains(c.CriterionId)).ToList();
                if (missing.Count > 0)
                    _logger.LogWarning(
                        "SC1: buổi {SessionId} có {Missing}/{Total} tiêu chí nội dung KHÔNG được câu hỏi "
                        + "nào nhắm tới ({Names}) — chúng sẽ bị loại khỏi điểm, {Seeds} câu gốc đã sinh",
                        session.Id, missing.Count, targetable.Count,
                        string.Join(", ", missing.Select(c => c.Name)), seedQuestions.Count);
            }

            // RAG grounding — resolve citation per-câu (questionIndex → citedChunkIds → {sourceUrl,sourceTitle}
            // từ tập grounding đã cấp; GUARD drop id lạ). grounded → mỗi câu có LIST (rỗng nếu AI không cite);
            // KHÔNG grounded → null (không đi đường grounding). Index citations khớp index generated (seed = đầu).
            var citationsByIndex = citations.ToDictionary(
                c => c.QuestionIndex, c => GroundingMapper.ResolveCitations(grounding, c.CitedChunkIds));

            // Lưu câu hỏi + set Ready, commit #2 (tách commit tránh concurrency).
            var seedStride = SeedOrderStride(session.MaxDeepPerQuestion);
            var questions = seedQuestions
                .Select((q, idx) => new PracticeQuestion
                {
                    Id = Guid.NewGuid(),
                    SessionId = session.Id,
                    OrderNo = idx * seedStride + 1,   // INT-17b — chừa chỗ cho chuỗi đào sâu (xem SeedOrderStride)
                    Content = q.Content,
                    TimeLimitSec = session.TimeLimitSec,   // F2 — theo lựa chọn của ứng viên
                    Kind = QuestionKind.Seed,
                    // grounded → list (rỗng = ungrounded, non-empty = grounded); không grounded → null.
                    GroundingRefs = grounded
                        ? (citationsByIndex.TryGetValue(idx, out var cits) ? cits : new List<Citation>())
                        : null,
                    // Nhãn tiêu chí nội dung. ⚠ GIỮ NGUYÊN 3 trạng thái — `?.ToList()` chứ KHÔNG
                    // `is {Count:>0} ? … : null`: quy `[]` về null ở đây là làm tính năng vô hiệu đúng
                    // ở nhóm câu cần nó nhất (câu xã giao vẫn bị chấm tiêu chí chuyên môn).
                    TargetCriterionIds = q.TargetCriterionIds?.ToList()
                })
                .ToList();

            _db.PracticeQuestions.AddRange(questions);
            session.Status = SessionStatus.Ready;

            // Con dấu phạm vi chấm (xem PracticeSession.ScoringScopeVersion). Đóng theo SỰ THẬT quan
            // sát được — có câu nào thực sự mang nhãn không — chứ không theo "code này đã hỗ trợ nhãn".
            // Buổi không có nhãn nào được chấm y hệt trước đây, đóng dấu 2 cho nó là báo động giả cho
            // BC15/F14/CAMP-10 (đúng lỗi "suy KHÁC từ KHÔNG BIẾT" mà BK23 cấm, chỉ theo chiều ngược lại).
            // `is not null` (kể cả `[]`): nhãn RỖNG cũng thu hẹp phạm vi — câu đó chỉ được chấm 4 tiêu
            // chí cách nói, tức điểm buổi này đã khác thước đo cũ.
            session.ScoringScopeVersion =
                questions.Any(q => q.TargetCriterionIds is not null)
                    ? ScopeVersionPerQuestion
                    : ScopeVersionFullRubric;

            await _db.SaveChangesAsync(ct);

            _logger.LogInformation(
                "Tạo session {SessionId} ({Cat}) với {Count} câu hỏi (cv={HasCv}, jd={HasJd})",
                session.Id, session.JobCategory, questions.Count,
                cvText != null, jdText != null);

            if (_consumeAtGeneration)
                await ConsumeQuietlyAsync(session.Id, ct);

            return MapToResponse(session, questions, new List<PracticeAnswer>());
        }
        catch (Exception ex)
        {
            // Bù trừ credit đã reserve: hoàn chỗ giữ để không treo credit ví User (PAY-5/PAY-11).
            // Best-effort — lỗi release chỉ log, KHÔNG che lỗi gốc (dùng CancellationToken.None để release
            // vẫn chạy kể cả khi lỗi gốc do ct bị hủy). Release idempotent nên an toàn khi nhánh gen-fail
            // (BK12) đã phát SessionAbandoned trước đó.
            try
            {
                await _reservationClient.ReleaseAsync(sessionId);
                _logger.LogInformation(
                    "P1-2: hoàn credit đã reserve cho session {SessionId} sau lỗi tạo session", sessionId);
            }
            catch (Exception releaseEx)
            {
                _logger.LogError(releaseEx,
                    "P1-2: hoàn credit thất bại cho session {SessionId} (lỗi gốc vẫn ném lại)", sessionId);
            }

            _logger.LogError(ex, "Tạo session {SessionId} thất bại sau khi reserve credit", sessionId);
            throw;
        }
    }

    // ── CREATE B2B: session gắn campaign_id + materialize tiêu chí campaign (I1) ──────
    // Câu hỏi + tiêu chí do Campaign cấp sẵn (không gọi AI sinh). rubric_criteria keyed by
    // campaign_id → dùng chung mọi session của campaign ⇒ materialize idempotent theo campaign.
    public async Task<PracticeSessionResponse> CreateCampaignSessionAsync(
        Guid candidateId, CreateCampaignSessionRequest request, CancellationToken ct = default)
    {
        if (request.Questions is null || request.Questions.Count == 0)
            throw new InvalidOperationException("Campaign session cần ít nhất 1 câu hỏi");
        if (request.Criteria is null || request.Criteria.Count == 0)
            throw new InvalidOperationException("Campaign session cần ít nhất 1 tiêu chí");

        var language = ValidateLanguage(request.Language);
        var seniority = ValidateSeniority(request.Seniority);

        await EnsureCapacityAsync(ct); // CreateCampaignSession chỉ được gọi khi tạo mới, không chặn resume.

        // BK14: reserve 1 credit ví ORG (owner=Org, PAY-6) TRƯỚC khi tạo session row — reserve-first
        // như B2C (BC2) để tránh orphan. sessionId cấp trước → reserve khoá idempotency theo đúng Id
        // session sẽ dùng (P4). Ví org hết credit → Payment 402 → InsufficientCreditException ném ở đây
        // ⇒ KHÔNG có row session (PAY-5). Consume/release sau đó do E7 xử theo owner của reservation.
        var sessionId = Guid.NewGuid();
        var reservation = await _reservationClient.ReserveAsync(
            ownerType: "Org", ownerId: request.OrgId, sessionId: sessionId, ct: ct);
        _logger.LogInformation(
            "BK14: reserve credit ví org {OrgId} cho session B2B {SessionId} (reservation {ReservationId})",
            request.OrgId, sessionId, reservation.ReservationId);

        // Reserve đã thành công (credit org đã giữ). Mọi lỗi sau đây → ReleaseAsync(sessionId) best-effort
        // (idempotent PAY-11) TRƯỚC khi ném lại, tránh treo credit org — đồng pattern B2C (P1-2).
        try
        {
            var maxDeepPerQuestion = Math.Max(0, request.MaxDeepPerQuestion ?? 0);

            // Phiên bản rubric buổi này bị chấm bằng. Campaign là NGUỒN QUYỀN LỰC DUY NHẤT — ở đây chỉ
            // CHÉP. `?? 1` phủ bản Campaign cũ chưa gửi field, và khớp đúng mọi row đang có trên prod
            // (materialize cũ hardcode Version = 1) ⇒ không đổi hành vi cho campaign hiện hữu.
            // ⚠ TUYỆT ĐỐI không tự tính max(Version)+1: materialize là LAZY nên Campaign có thể đã ở
            // v3 khi Interview mới có v1; tự đánh số sẽ ra v2 ⇒ số HR thấy và số trên answer_scores
            // lệch nhau vĩnh viễn (lớp lỗi BK23).
            var pinnedRubricVersion = request.RubricVersion ?? 1;

            var session = new PracticeSession
            {
                Id = sessionId,
                CandidateId = candidateId,
                CampaignId = request.CampaignId,
                // Ghim DÙ CÓ materialize hay không: buổi thứ hai trở đi của cùng phiên bản không
                // materialize gì cả, nhưng vẫn phải biết mình đang bị chấm bằng thước nào.
                CampaignRubricVersion = pinnedRubricVersion,
                JobCategory = request.JobCategory,
                Seniority = seniority,
                Language = language,
                Status = SessionStatus.Ready,   // câu hỏi cấp sẵn → không cần sinh AI
                CreatedAt = DateTime.UtcNow,
                Deadline = request.ExpiresAt,   // I2: hạn chót nhận bài (B2B); null → không hard-deadline
                // Phỏng vấn THÍCH ỨNG (B2B): Campaign/HR bật → seed = TOÀN BỘ campaign questions (ai cũng
                // nhận cùng bộ, công bằng), chấm theo cùng tiêu chí. null → tắt.
                // INT-17b: `MaxDeepPerQuestion > 0` → mỗi câu campaign có chuỗi đào sâu XEN KẼ ngay sau nó
                // (thay vì dồn ở đuôi buổi); vẫn công bằng vì mọi ứng viên nhận cùng bộ câu gốc và cùng trần.
                AdaptiveEnabled = request.AdaptiveEnabled ?? false,
                MaxQuestions = ClampCampaignMaxQuestions(request.MaxQuestions, request.CampaignId),
                // INT-17b — ĐỐI XỨNG đường B2C (:195-199): ở chế độ chuỗi, trần theo BUỔI phải để 0.
                // Để nguyên giá trị HR khai thì nó bó chặt hơn trần theo CÂU, vì `AnswerService` đếm
                // `followUpCount` trên MỌI câu non-Seed của cả buổi ⇒ ngân sách cạn giữa chuỗi. Đo được:
                // campaign maxDeep=2 + maxFollowUps=3, 4 câu gốc → phân bố câu sâu 2/1/0/0 PHỤ THUỘC
                // THỨ TỰ TRẢ LỜI ⇒ hai ứng viên cùng campaign nhận số câu và chủ đề khác nhau, trong khi
                // điểm vẫn đem xếp hạng chung (CAMP-10). `MaxQuestions` mới là trần buổi.
                // Điều kiện MỘT vế, source-independent: đường B2B không đọc entitlement (Campaign chỉ
                // CHẶN lúc HR bật, không CẤP giá trị; EntitlementSnapshot không có MaxDeepPerQuestion).
                MaxFollowUps = maxDeepPerQuestion > 0 ? 0 : (request.MaxFollowUps ?? 0),
                MaxDeepPerQuestion = maxDeepPerQuestion,
                // B2B: câu hỏi do HR/Campaign cấp sẵn, KHÔNG đi qua đường gắn nhãn ⇒ chấm trên toàn bộ
                // tiêu chí campaign, y như trước. Đóng dấu 1 ("đã biết: full rubric") chứ không để null:
                // null nghĩa là "không biết", mà ở đây ta biết chắc. Quan trọng cho CAMP-10 — xếp hạng
                // trộn ứng viên trước/sau mốc deploy này vẫn so sánh được, và có dữ liệu để chứng minh.
                ScoringScopeVersion = ScopeVersionFullRubric
            };
            _db.PracticeSessions.Add(session);

            var seedStride = SeedOrderStride(session.MaxDeepPerQuestion);
            var questions = request.Questions
                .Select((content, idx) => new PracticeQuestion
                {
                    Id = Guid.NewGuid(),
                    SessionId = session.Id,
                    OrderNo = idx * seedStride + 1,   // INT-17b — chừa chỗ cho chuỗi đào sâu
                    Content = content,
                    // SNAPSHOT đáp án mẫu: chép xuống buổi thi, không đọc live từ Campaign lúc chấm —
                    // đáp án là một phần THƯỚC ĐO, đọc live thì hai ứng viên cùng chiến dịch có thể bị
                    // chấm theo hai bản khác nhau mà điểm vẫn xếp chung một bảng (CAMP-10).
                    // Controller đã bảo đảm QuestionDetails khớp số lượng với Questions, hoặc là null.
                    SampleAnswer = request.QuestionDetails?[idx].SampleAnswer,
                    TimeLimitSec = DefaultTimeLimitSec,
                    Kind = QuestionKind.Seed
                })
                .ToList();
            _db.PracticeQuestions.AddRange(questions);

            // Materialize tiêu chí campaign → rubric_criteria(campaign_id), idempotent theo
            // (campaign, PHIÊN BẢN). HR sửa mốc ⇒ Campaign bump version ⇒ ứng viên kế tiếp Start sẽ
            // materialize bộ mới; buổi đang chạy dở giữ bộ cũ nhờ pin (xem RubricCriteriaLoader).
            var alreadyMaterialized = await _db.RubricCriteria
                .AnyAsync(c => c.CampaignId == request.CampaignId && c.Version == pinnedRubricVersion, ct);
            if (!alreadyMaterialized)
            {
                // Hạ cờ bộ cũ = "không dùng cho buổi thi MỚI nữa". KHÔNG hard-delete: answer_scores có
                // FK Restrict trỏ vào criterion, và điểm đã chấm phải giữ được lai lịch thước đo của nó.
                // Mẫu soft-versioning đã dùng ở rubric riêng B2C (RubricLibraryService, BC16).
                var superseded = await _db.RubricCriteria
                    .Where(c => c.CampaignId == request.CampaignId && c.IsActive)
                    .ToListAsync(ct);
                foreach (var old in superseded) old.IsActive = false;

                var criteria = request.Criteria.Select(c => new RubricCriterion
                {
                    Id = Guid.NewGuid(),
                    Name = c.Name,
                    Description = c.Description,
                    Weight = c.Weight,
                    MaxScore = c.MaxScore,
                    IsActive = true,
                    JobCategory = request.JobCategory,   // cột bắt buộc; B2B chấm theo campaign_id
                    CampaignId = request.CampaignId,
                    Language = language,
                    Version = pinnedRubricVersion,
                    // E9 — mốc điểm HR soạn. Rỗng/null ⇒ không tạo level nào ⇒ AIService rơi về dải
                    // mặc định 0..maxScore y như trước (không có mốc là trạng thái HỢP LỆ, không lỗi).
                    Levels = (c.Levels ?? [])
                        .Select(l => new RubricLevel
                        {
                            Id = Guid.NewGuid(),
                            Score = l.Score,
                            Descriptor = l.Descriptor,
                            ExampleAnswers = []   // anchor cố ý chưa dùng ở vòng này
                        })
                        .ToList()
                });
                _db.RubricCriteria.AddRange(criteria);
            }

            await _db.SaveChangesAsync(ct);

            // Evidence-driven B2B: snapshot toàn bộ campaign rubric; B2B chấm đủ rubric, khác
            // LoadTargetableCriteriaAsync vốn chỉ đúng cho rubric riêng B2C.
            if (session.AdaptiveEnabled)
            {
                // Theo ĐÚNG phiên bản buổi này ghim, không theo is_active: hai thứ đó đã tách nghĩa từ
                // khi có versioning, và evidence phải mô tả bộ tiêu chí ứng viên này thực sự bị chấm.
                var evidenceCriteria = await _db.RubricCriteria.AsNoTracking()
                    .Where(c => c.CampaignId == request.CampaignId && c.Version == pinnedRubricVersion)
                    .OrderBy(c => c.Name)
                    .Select(c => new { c.Id, c.Name })
                    .ToListAsync(ct);
                _db.SessionCriterionEvidence.AddRange(evidenceCriteria.Select(c => new SessionCriterionEvidence
                {
                    SessionId = session.Id, CriterionId = c.Id, CriterionName = c.Name, State = "UNKNOWN"
                }));
                await _db.SaveChangesAsync(ct);
            }

            _logger.LogInformation(
                "Tạo session B2B {SessionId} cho campaign {CampaignId} ({Q} câu, materialize criteria={Mat})",
                session.Id, request.CampaignId, questions.Count, !alreadyMaterialized);

            if(_consumeAtGeneration)
                await ConsumeQuietlyAsync(session.Id, ct);

            return MapToResponse(session, questions, new List<PracticeAnswer>());
        }
        catch (Exception ex)
        {
            try
            {
                await _reservationClient.ReleaseAsync(sessionId);
                _logger.LogInformation(
                    "BK14: hoàn credit org đã reserve cho session B2B {SessionId} sau lỗi tạo session", sessionId);
            }
            catch (Exception releaseEx)
            {
                _logger.LogError(releaseEx,
                    "BK14: hoàn credit org thất bại cho session B2B {SessionId} (lỗi gốc vẫn ném lại)", sessionId);
            }

            _logger.LogError(ex, "Tạo session B2B {SessionId} thất bại sau khi reserve credit org", sessionId);
            throw;
        }
    }

    // ── CREATE-OR-GET B2B (D2): idempotent theo (candidateId, campaignId) ─────────────
    // Campaign /start có thể gọi nhiều lần (ứng viên refresh / bấm lại) — trả CÙNG session đang mở
    // thay vì đẻ session mới. "Đang mở" = chưa terminal (Scored/Failed/SessionAbandoned). Hết mở →
    // tạo session mới (I1). KHÔNG dùng UNIQUE DB (race hiếm chấp nhận được ở scope này) — dedup bằng query.
    public async Task<PracticeSessionResponse> GetOrCreateCampaignSessionAsync(
        Guid candidateId, CreateCampaignSessionRequest request, CancellationToken ct = default)
    {
        var existing = await _db.PracticeSessions
            .FirstOrDefaultAsync(s =>
                s.CandidateId == candidateId
                && s.CampaignId == request.CampaignId
                && s.Status != SessionStatus.Scored
                && s.Status != SessionStatus.Failed
                && s.Status != SessionStatus.SessionAbandoned, ct);

        if (existing is null)
            return await CreateCampaignSessionAsync(candidateId, request, ct);

        var questions = await _db.PracticeQuestions.AsNoTracking()
            .Where(q => q.SessionId == existing.Id)
            .OrderBy(q => q.OrderNo)
            .ToListAsync(ct);

        var answers = await _db.PracticeAnswers.AsNoTracking()
            .Include(a => a.Scores).ThenInclude(sc => sc.Criterion)
            .AsSplitQuery()   // DB31: tránh 1 JOIN lặp transcript (TEXT) trên answers×scores×criteria
            .Where(a => a.SessionId == existing.Id)
            .ToListAsync(ct);

        _logger.LogInformation(
            "create-or-get: trả session B2B đang mở {SessionId} (candidate {CandidateId}, campaign {CampaignId})",
            existing.Id, candidateId, request.CampaignId);

        return MapToResponse(existing, questions, answers);
    }

    private async Task EnsureCapacityAsync(CancellationToken ct)
    {
        if (_capacity.MaxConcurrentSessions <= 0) return;

        var running = await _db.PracticeSessions.CountAsync(s =>
            s.Status == SessionStatus.GeneratingQuestions || s.Status == SessionStatus.Ready ||
            s.Status == SessionStatus.InProgress, ct);
        if (running >= _capacity.MaxConcurrentSessions)
            throw new CapacityExceededException(_capacity.MaxConcurrentSessions);
    }

    // ── SUBMIT SESSION: chốt sổ (KHÔNG publish — chấm dần đã publish lúc upload) ──
    public async Task SubmitSessionAsync(
        Guid candidateId, Guid sessionId, CancellationToken ct = default)
    {
        var session = await _db.PracticeSessions
            .FirstOrDefaultAsync(s => s.Id == sessionId, ct)
            ?? throw new KeyNotFoundException("Session không tồn tại");

        if (session.CandidateId != candidateId)
            throw new UnauthorizedAccessException("Không phải buổi của bạn");

        if (session.Status is not (SessionStatus.Ready or SessionStatus.InProgress))
            throw new InvalidOperationException(
                $"Buổi ở trạng thái {session.Status}, không thể nộp");

        // INT-5: cần ≥1 câu trả lời THẬT mới nộp được (đếm trước khi tạo Skipped bên dưới).
        var hasAnswer = await _db.PracticeAnswers.AnyAsync(a => a.SessionId == sessionId, ct);
        if (!hasAnswer)
            throw new InvalidOperationException("Chưa trả lời câu nào, không thể nộp");

        // I2 (D21): chốt buổi theo TỪNG CÂU — câu CHƯA có answer → đánh `Skipped` (không chặn đóng buổi;
        // câu có audio giữ nguyên trạng thái đang chấm). Skipped tính là "done" ở allDone bên dưới.
        await MarkUnansweredAsSkippedAsync(sessionId, ct);

        // Chấm dần: mỗi answer đã được publish ngay lúc upload (AnswerService).
        // SubmitSession chỉ chốt sổ — KHÔNG publish lại để tránh chấm trùng.
        session.Status = SessionStatus.Scoring;
        session.CompletedAt = DateTime.UtcNow;
        await _db.SaveChangesAsync(ct);

        // Race của chấm dần: answer cuối có thể đã Scored TRƯỚC khi user bấm submit,
        // khi đó callback không đóng session (lúc đó session còn InProgress).
        // Phải kiểm tra ngay để đóng, tránh session kẹt Scoring vì không còn callback.
        var statuses = await _db.PracticeAnswers
            .Where(a => a.SessionId == sessionId)
            .Select(a => a.Status)
            .ToListAsync(ct);

        bool allDone = statuses.All(s =>
            s is AnswerStatus.Scored or AnswerStatus.Skipped or AnswerStatus.Failed);

        if (!allDone)
        {
            _logger.LogInformation("Chốt session {SessionId} -> Scoring (đang chờ chấm nốt)", sessionId);
            return;
        }

        // PAY-13: nhánh "đóng-ngay" của submit (mọi answer đã terminal lúc submit) — nếu KHÔNG answer
        // nào Scored (mọi answer Failed/Skipped) → phát SessionAbandoned (release), không consume credit
        // cho buổi 0 answer chấm được (PAY-1). Đối xứng với AnswerService.TryCompleteSessionAsync.
        var scoredCount = statuses.Count(s => s == AnswerStatus.Scored);
        if (scoredCount == 0)
        {
            // DB2: đóng session (state) + ghi outbox-row abandoned CÙNG 1 SaveChanges (atomic).
            session.Status = SessionStatus.SessionAbandoned;
            await _scoringNotifier.EnqueueSessionAbandonedAsync(sessionId, "no_scored_answer", ct);
            await _db.SaveChangesAsync(ct);
            _logger.LogInformation(
                "Session {SessionId} -> SessionAbandoned ngay khi submit (không answer nào Scored)", sessionId);
            return;
        }

        // DB2: đóng session Scored (state) + ghi outbox-row SessionScored CÙNG 1 SaveChanges (atomic).
        session.Status = SessionStatus.Scored;
        await _scoringNotifier.EnqueueSessionScoredAsync(sessionId, ct);
        await _db.SaveChangesAsync(ct);
        _logger.LogInformation(
            "Session {SessionId} -> Scored ngay khi submit (đã chấm xong từ trước)", sessionId);

        // BC9/BC10/BC14/BC15: side-effect best-effort SAU khi đã commit (không chặn đóng session).
        await _scoringNotifier.NotifySessionScoredAsync(sessionId, ct);
    }

    // I2 (D21) per-question finalize: mọi câu của buổi CHƯA có answer → tạo answer `Skipped`
    // (không audio, DurationSec=0). Dùng khi chốt buổi (manual submit + sweeper auto-submit) để câu
    // trống không kẹt buổi ở Scoring. Câu đã có answer (Uploaded/Scoring/Scored/Failed) KHÔNG đụng.
    // Add vào context (KHÔNG SaveChanges) — caller lưu chung trong lần SaveChanges chốt buổi.
    private async Task MarkUnansweredAsSkippedAsync(Guid sessionId, CancellationToken ct)
    {
        var answeredQuestionIds = await _db.PracticeAnswers
            .Where(a => a.SessionId == sessionId)
            .Select(a => a.QuestionId)
            .ToListAsync(ct);

        var unansweredQuestionIds = await _db.PracticeQuestions
            .Where(q => q.SessionId == sessionId && !answeredQuestionIds.Contains(q.Id))
            .Select(q => q.Id)
            .ToListAsync(ct);

        if (unansweredQuestionIds.Count == 0) return;

        var now = DateTime.UtcNow;
        _db.PracticeAnswers.AddRange(unansweredQuestionIds.Select(qid => new PracticeAnswer
        {
            Id = Guid.NewGuid(),
            SessionId = sessionId,
            QuestionId = qid,
            Status = AnswerStatus.Skipped,
            DurationSec = 0,
            CreatedAt = now
        }));

        _logger.LogInformation(
            "Chốt buổi {SessionId}: đánh {Count} câu chưa trả lời là Skipped", sessionId, unansweredQuestionIds.Count);
    }

    // ── GET ───────────────────────────────────────────────────────────────
    public async Task<PracticeSessionResponse?> GetSessionAsync(
        Guid candidateId, Guid sessionId, CancellationToken ct = default)
    {
        var session = await _db.PracticeSessions
            .AsNoTracking()
            .FirstOrDefaultAsync(s => s.Id == sessionId, ct);

        if (session is null) return null;
        if (session.CandidateId != candidateId)
            throw new UnauthorizedAccessException("Không phải buổi của bạn");

        var questions = await _db.PracticeQuestions
            .AsNoTracking()
            .Where(q => q.SessionId == sessionId)
            .OrderBy(q => q.OrderNo)
            .ToListAsync(ct);

        var answers = await _db.PracticeAnswers
            .AsNoTracking()
            .Include(a => a.Scores).ThenInclude(sc => sc.Criterion)
            .AsSplitQuery()   // DB31: tránh 1 JOIN lặp transcript (TEXT) trên answers×scores×criteria
            .Where(a => a.SessionId == sessionId)
            .ToListAsync(ct);

        // BC9: tổng kết buổi chỉ áp B2C đã Scored — đọc thẳng breakdown từ DB (không tính lại).
        var isB2CScored = session.Status == SessionStatus.Scored && session.CampaignId is null;
        var criterionScores = isB2CScored
            ? await _db.SessionCriterionScores.AsNoTracking()
                .Where(x => x.SessionId == sessionId)
                .ToListAsync(ct)
            : new List<SessionCriterionScore>();

        // BC8: đối chiếu CV↔trả lời — chỉ B2C đã Scored & có CV đã phân tích (BC7). ĐỌC dữ liệu sẵn
        // có (không AI): lấy phân tích CV mới nhất cho đúng CvId của buổi (join lỏng qua CvId+chủ).
        IReadOnlyList<string> cvStrengths = Array.Empty<string>();
        if (isB2CScored && session.CvId is not null)
        {
            var cv = await _db.CvAnalyses.AsNoTracking()
                .Where(x => x.CvId == session.CvId && x.CandidateId == session.CandidateId)
                .OrderByDescending(x => x.CreatedAt)
                .FirstOrDefaultAsync(ct);
            if (cv is not null)
                cvStrengths = MergeStrengths(cv);
        }

        // F14 — mốc đối chiếu (lớp 2 của radar). Chỉ dựng cho B2C đã Scored (cùng điều kiện với
        // BC9 breakdown); thuần đọc, không ghi DB.
        var benchmark = isB2CScored && _benchmarks is not null
            ? await _benchmarks.BuildAsync(session, criterionScores, ct)
            : null;

        var criterionEvidence = await _db.SessionCriterionEvidence.AsNoTracking()
            .Where(x => x.SessionId == sessionId)
            .OrderBy(x => x.CriterionName)
            .Select(x => new CriterionEvidenceResponse(
                x.CriterionId, x.CriterionName, x.State,
                x.EvidenceFound, x.MissingEvidence, x.DeepCount, x.UpdatedAt))
            .ToListAsync(ct);

        return MapToResponse(session, questions, answers, criterionScores, cvStrengths, benchmark, criterionEvidence);
    }

    // ── HISTORY ───────────────────────────────────────────────────────────
    // DB31 — keyset-paged (mẫu DB8, dùng chung Isas.Shared/Pagination). Trước đây KHÔNG có
    // Skip/Take/cursor → trả TOÀN BỘ lịch sử phỏng vấn trọn đời của candidate trong 1 payload.
    // Backward-compat y hệt DB8: body vẫn là mảng JSON, cursor opaque + limit là opt-in
    // (`?cursor=&limit=`), next-cursor ở header X-Next-Cursor, limit mặc định = trần cũ.
    public async Task<KeysetPage<PracticeSessionSummary>> GetHistoryAsync(
        Guid candidateId, string? cursor = null, int? limit = null,
        string? status = null, bool? excludeCampaign = null, CancellationToken ct = default)
    {
        var take = KeysetPaging.ClampLimit(limit);
        var cur = KeysetCursor.Decode(cursor);

        var query = _db.PracticeSessions
            .AsNoTracking()
            .Where(s => s.CandidateId == candidateId);

        // Opt-in — mẫu ListAllCampaignsAsync (CampaignService): parse fail-open, giá trị lạ
        // KHÔNG parse được ⇒ filter đơn giản không được áp (trả nguyên, không lọc gì) thay vì
        // 400 (đây là filter duyệt-danh-sách, không phải input dẫn nghiệp vụ).
        if (!string.IsNullOrWhiteSpace(status)
            && Enum.TryParse<SessionStatus>(status.Trim(), ignoreCase: true, out var parsedStatus))
            query = query.Where(s => s.Status == parsedStatus);

        // Loại buổi B2B (campaign_id != null) — dùng cho wizard roadmap: CreateAsync chỉ nhận
        // buổi B2C (CampaignId == null), nên picker phải loại B2B TRƯỚC khi người dùng chọn được,
        // thay vì để họ ăn 404 batch không nói id nào sai (RoadmapService.CreateAsync).
        //
        // DÙNG LẠI CHÍNH expression object của RoadmapSessionEligibility (không chép tay lần thứ
        // hai): nó cũng là vế "không phải campaign" trong RoadmapService.CreateAsync — lệch nhau
        // một chữ giữa hai nơi là picker cho chọn buổi mà CreateAsync sẽ từ chối bằng 404 mù.
        if (excludeCampaign == true)
            query = query.Where(RoadmapSessionEligibility.NotCampaign);

        if (cur is not null)
            query = query.Where(s => s.CreatedAt < cur.CreatedAt
                || (s.CreatedAt == cur.CreatedAt && s.Id.CompareTo(cur.Id) < 0));

        var rows = await query
            .OrderByDescending(s => s.CreatedAt)
            .ThenByDescending(s => s.Id)
            .Take(take)
            .Select(s => new PracticeSessionSummary(
                s.Id, s.Status.ToString(), s.JobCategory.ToString(),
                s.CreatedAt, s.CompletedAt, s.OverallScore,   // BC9: lịch sử hiện điểm tổng
                s.Seniority))   // J8: cấp độ đã chọn CHO ĐÚNG BUỔI ĐÓ
            .ToListAsync(ct);

        var next = rows.Count == take
            ? new KeysetCursor(rows[^1].CreatedAt, rows[^1].Id).Encode()
            : null;
        return new KeysetPage<PracticeSessionSummary>(rows, next);
    }

    public async Task<AnswerAudioContent?> GetAnswerAudioAsync(
        Guid candidateId, Guid sessionId, Guid answerId, CancellationToken ct = default)
    {
        var answer = await _db.PracticeAnswers
            .AsNoTracking()
            .Include(a => a.Session)
            .FirstOrDefaultAsync(a => a.Id == answerId && a.SessionId == sessionId, ct);

        if (answer is null || string.IsNullOrWhiteSpace(answer.AudioObjectKey))
            return null;
        if (answer.Session.CandidateId != candidateId)
            throw new UnauthorizedAccessException("Không phải buổi của bạn");

        var content = await _storage.DownloadAsync(answer.AudioObjectKey, ct);
        // MIME suy từ đuôi của object key — trước đây trả cứng "audio/webm" cho MỌI file, nên bản ghi âm từ
        // iPhone (m4a) được gắn nhãn webm và trình phát từ chối. Đuôi là nguồn duy nhất còn giữ được định dạng:
        // IStorageService.DownloadAsync chỉ trả Stream, không kèm content-type của S3.
        return new AnswerAudioContent(content, AudioFormats.ContentTypeForKey(answer.AudioObjectKey));
    }

    // DB18 — Payment (internal) dò orphan reservation: trả TẬP CON sessionIds có row practice_sessions
    // (bất kể status). Reservation Reserved mà session KHÔNG tồn tại (crash giữa reserve↔insert lúc Start)
    // = orphan → Payment release. Distinct để không phụ thuộc caller; rỗng → rỗng (không query).
    public async Task<IReadOnlyList<Guid>> GetExistingSessionIdsAsync(
        IReadOnlyList<Guid> sessionIds, CancellationToken ct = default)
    {
        if (sessionIds is null || sessionIds.Count == 0)
            return Array.Empty<Guid>();

        var ids = sessionIds.Distinct().ToList();
        return await _db.PracticeSessions
            .AsNoTracking()
            .Where(s => ids.Contains(s.Id))
            .Select(s => s.Id)
            .ToListAsync(ct);
    }

    // R1 — như GetExistingSessionIdsAsync nhưng kèm trạng thái (string tên enum, GEN-2). Payment dùng để
    // phân nhánh chỗ giữ mồ côi: Scored → consume (buổi đã được AI chấm, PAY-1/PAY-13) · SessionAbandoned/
    // Failed → release · đang bay → SKIP. Cùng vị ngữ query để 2 hàm không bao giờ lệch tập session.
    public async Task<IReadOnlyList<SessionStateDto>> GetExistingSessionStatesAsync(
        IReadOnlyList<Guid> sessionIds, CancellationToken ct = default)
    {
        if (sessionIds is null || sessionIds.Count == 0)
            return Array.Empty<SessionStateDto>();

        var ids = sessionIds.Distinct().ToList();
        var rows = await _db.PracticeSessions
            .AsNoTracking()
            .Where(s => ids.Contains(s.Id))
            .Select(s => new { s.Id, s.Status })
            .ToListAsync(ct);

        // ToString() ở client-side: provider dịch enum→string khác nhau (Npgsql lưu string, SQLite có thể
        // ra số) → materialize rồi map để tên trạng thái trên dây LUÔN là tên enum C#.
        return rows.Select(r => new SessionStateDto(r.Id, r.Status.ToString())).ToList();
    }

    // AI4 — INTERNAL (Campaign/HR): trả per-question list kèm transcript + nhận xét AI per-criterion +
    // cờ needs_review (E10/E11). Tái dùng NGUYÊN VẸN truy vấn + MapAnswer của GetSessionAsync (một nguồn
    // sự thật cho transcript/điểm) NHƯNG BỎ check chủ session — caller là máy-máy (X-Internal-Token) và
    // Campaign đã gate org+ranking. MapToResponse với criterionScores/cvStrengths mặc định null → phần
    // Result (BC9/BC8) = null; ta chỉ lấy .Questions. Session không tồn tại → null (controller 404).
    public async Task<IReadOnlyList<QuestionResponse>?> GetSessionAnswersInternalAsync(
        Guid sessionId, CancellationToken ct = default)
    {
        var session = await _db.PracticeSessions
            .AsNoTracking()
            .FirstOrDefaultAsync(s => s.Id == sessionId, ct);

        if (session is null) return null;

        var questions = await _db.PracticeQuestions
            .AsNoTracking()
            .Where(q => q.SessionId == sessionId)
            .OrderBy(q => q.OrderNo)
            .ToListAsync(ct);

        var answers = await _db.PracticeAnswers
            .AsNoTracking()
            .Include(a => a.Scores).ThenInclude(sc => sc.Criterion)
            .AsSplitQuery()   // DB31: tránh 1 JOIN lặp transcript (TEXT) trên answers×scores×criteria
            .Where(a => a.SessionId == sessionId)
            .ToListAsync(ct);

        return MapToResponse(session, questions, answers).Questions;
    }

    // ── helpers ───────────────────────────────────────────────────────────

    // BK12: B2C reserve credit ví cá nhân (BC2) TRƯỚC khi sinh câu hỏi. Nếu AI sinh câu hỏi lỗi →
    // session `Failed`, credit đang bị KẸT: E3 sweeper chỉ quét `InProgress`, còn `Failed` KHÔNG tự
    // phát `SessionAbandoned` → E7 (Payment) không release → orphan credit. Fix: ghi outbox-row
    // `SessionAbandoned(reason=generation_failed)` để OutboxDispatcher phát → E7 hoàn credit ví User.
    // DB2: ghi outbox-row CÙNG SaveChanges với state=Failed (atomic — broker chết vẫn còn row để gửi lại).
    // SettlementReconciler cũ BỎ SÓT site này (chỉ quét Scored/SessionAbandoned); outbox phủ cả nó. Chỉ
    // B2C dùng path này (CreateSessionAsync); B2B không reserve (PAY-6) và không có nhánh Failed-sau-reserve.
    private async Task EnqueueGenerationFailedAbandonAsync(PracticeSession session, CancellationToken ct)
    {
        await _scoringNotifier.EnqueueSessionAbandonedAsync(session.Id, GenerationFailedReason, ct);
        await _db.SaveChangesAsync(ct);   // atomic: state=Failed + outbox-row
        _logger.LogInformation(
            "BK12: ghi outbox SessionAbandoned(generation_failed) cho session {SessionId} để release credit ví User",
            session.Id);

        // BC14 (defense-in-depth): nếu session này đang gắn 1 roadmap lesson (Practicing) mà sinh câu
        // hỏi lỗi → trả lesson về Theory + clear session_id để /start lại được. Luồng /start hiện link
        // lesson SAU khi tạo session xong (FK), nên gen-fail thường CHƯA link → no-op; giữ để an toàn
        // nếu thứ tự đổi. Best-effort (nuốt lỗi — session đã Failed trong DB).
        await RevertLinkedLessonAsync(session.Id, ct);
    }

    // BC14 — reset lesson đang gắn 1 session không-Scored về Theory (start lại được). Guard theo
    // session_id + status Practicing → chỉ chạm lesson đang luyện đúng session này (no-op nếu không có).
    private async Task RevertLinkedLessonAsync(Guid sessionId, CancellationToken ct)
    {
        try
        {
            await _db.RoadmapLessons
                .Where(l => l.SessionId == sessionId && l.Status == LessonStatus.Practicing)
                .ExecuteUpdateAsync(u => u
                    .SetProperty(l => l.Status, LessonStatus.Theory)
                    .SetProperty(l => l.SessionId, (Guid?)null), ct);
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "BC14: revert lesson về Theory thất bại cho session {SessionId}", sessionId);
        }
    }

    // Chuẩn hoá text nhập tay: rỗng/toàn khoảng trắng = KHÔNG nhập (null), còn lại thì trim.
    // Giống hệt CampaignService.NormalizeText (C11) → "gửi jdText rỗng" hành xử như không gửi ở cả 2 dòng.
    // + cap độ dài (TextInputLimits.JdTextMaxChars — ngưỡng CHUNG với B2B/Campaign): JD nhập tay đi thẳng
    // vào prompt Gemini → vượt ngưỡng ném InvalidOperationException (controller map → 400) kèm giới hạn và
    // độ dài đang gửi. Đo SAU khi trim → khoảng trắng thừa không tính vào ngưỡng.
    private static string? NormalizeText(string? text)
        => TextInputLimits.NormalizeAndEnsureLimit(
            text, JdTextLabel, msg => new InvalidOperationException(msg));

    // Nhãn field trong thông báo lỗi 400 — khớp tên field client gửi lên.
    private const string JdTextLabel = "Mô tả công việc (jdText)";

    // RAG grounding — dựng câu truy vấn embed (RETRIEVAL_QUERY). Ưu tiên tín hiệu chủ đề: jobCategory +
    // focusCriteria (lesson) + JD (ưu tiên) hoặc CV. RetrieveAsync tự cắt độ dài. Query VN↔EN cross-lingual
    // (gemini-embedding-001) nên không cần dịch.
    private static string BuildRetrievalQuery(
        string jobCategory, string? cvText, string? jdText, IReadOnlyList<string>? focusCriteria)
    {
        var parts = new List<string> { jobCategory };
        if (focusCriteria is { Count: > 0 })
            parts.Add(string.Join(", ", focusCriteria));
        if (!string.IsNullOrWhiteSpace(jdText)) parts.Add(jdText);
        else if (!string.IsNullOrWhiteSpace(cvText)) parts.Add(cvText);
        return string.Join("\n", parts);
    }

    // F2 — thời lượng mỗi câu ứng viên được chọn. Tập ĐÓNG (không phải khoảng): 3 mốc để UI là nhóm nút
    // chọn, và để mọi buổi so sánh được với nhau. Tập nằm ở tầng service chứ KHÔNG đưa vào CHECK của DB —
    // đổi lựa chọn sau này (thêm 180s chẳng hạn) sẽ phải chạy migration chỉ để sửa một danh sách UI.
    private static readonly int[] AllowedTimeLimitsSec = [60, 120, 240];

    // null = client cũ không gửi → giữ mặc định "vi" (hành vi trước bilingual, không phải lỗi).
    // Chuỗi RỖNG/toàn khoảng trắng là một GIÁ TRỊ SAI, KHÔNG được coi như "không gửi" — cùng lớp
    // lỗi BK35 (ValidateLanguage phía Campaign) và cùng mẫu ValidateSeniority ngay trên: dùng
    // `IsNullOrWhiteSpace` gộp "không gửi" với "gửi rỗng" làm caller gõ `language: ""` (vd lỗi
    // client, hoặc field bị xoá tay) ÂM THẦM nhận "vi" thay vì bị từ chối — sai ở đúng chỗ khó
    // phát hiện nhất vì HTTP vẫn 200.
    // ⚠ Ném InvalidOperationException chứ KHÔNG phải ArgumentException: PracticeController chỉ bắt
    // InvalidOperationException → 400; ArgumentException rơi xuống catch(Exception) → 500. Cùng kiểu với
    // guard jobCategory ngay đầu CreateSessionInternalAsync.
    private string ValidateLanguage(string? requested)
    {
        if (requested is null) return "vi";
        var language = requested.Trim().ToLowerInvariant();
        if (language is not ("vi" or "en"))
            throw new InvalidOperationException("language chỉ nhận vi hoặc en.");
        if (!_bilingualEnabled && language != "vi")
            throw new InvalidOperationException("Bilingual interview chưa được bật.");
        return language;
    }

    // A session must never reserve a credit for a language whose B2C rubric has not been deployed.
    // Otherwise scoring has no criteria, leaves the answer Uploaded forever, and consumes the credit.
    // Con dấu phạm vi chấm (practice_sessions.scoring_scope_version). Xem entity để biết ý nghĩa
    // từng giá trị và vì sao null KHÔNG được đọc là "khác phiên bản".
    private const int ScopeVersionFullRubric = 1;    // đã biết: chấm trên toàn bộ rubric
    private const int ScopeVersionPerQuestion = 2;   // đã biết: có câu chấm trên tập tiêu chí hẹp hơn

    /// <summary>
    /// Tiêu chí NỘI DUNG (<see cref="ScoringScope.WhenTargeted"/>) của rubric B2C đang hiệu lực cho
    /// (candidate, nghề, ngôn ngữ) — cấp cho AIService để gắn nhãn câu hỏi.
    ///
    /// Dùng CHUNG <see cref="B2CRubricScope"/> với 6 site chọn tiêu chí kia (publish · callback ·
    /// republisher · BC9 · weighted-total · Q9): resolve khác nhau ở đây thì nhãn sẽ trỏ vào id của
    /// một bộ rubric KHÁC bộ dùng để chấm ⇒ lọc phạm vi không khớp được id nào.
    /// </summary>
    /// <summary>
    /// Số câu GỐC của buổi ở chế độ chuỗi đào sâu (INT-17b + SC1).
    ///
    /// <para>Ba lực kéo, xử theo đúng thứ tự này:</para>
    /// <list type="number">
    ///   <item><b>Ngân sách</b> — <paramref name="maxQuestions"/> là TỔNG số câu của buổi (F2b), nên
    ///   số câu gốc phải chia cho chiều sâu (làm tròn LÊN để tiêu hết ngân sách): trần 20 → 5 gốc
    ///   (5×4=20) · 10 → 3 · 5 → 2. Lấy thẳng <c>SeedCount</c> thì FE mặc định 5 câu sẽ sinh 5 câu gốc,
    ///   <c>askedCount</c> chạm trần ngay ⇒ 0 câu đào sâu, không lỗi, không báo (bug BUS-01).</item>
    ///   <item><b>Phủ tiêu chí (SC1)</b> — <paramref name="contentCriteriaCount"/> là SÀN, và nó THẮNG
    ///   trần <c>SeedCount</c>. Tiêu chí nội dung không câu nào hỏi sẽ bị loại khỏi điểm, nên điểm
    ///   thành "may mắn trúng tủ"; thiếu đào sâu chỉ mất chiều sâu. Đo trên prod: 3 câu gốc nhưng nhãn
    ///   ra 2 lần cùng một tiêu chí ⇒ tiêu chí thứ ba không bao giờ được hỏi. Bám con số ĐỘNG chứ không
    ///   hằng số config vì BC16 cho candidate tự CRUD rubric — hằng số sẽ lệch âm thầm.</item>
    ///   <item><b>Chừa khe đào sâu</b> — trần cứng <c>budget - 1</c>. Để <c>seeds == budget</c> thì
    ///   <c>AnswerService</c> thấy hết ngân sách ngay lượt đầu ⇒ buổi đóng dấu adaptive nhưng chạy y
    ///   như batch tĩnh. Đây là vế thắng sau cùng: ngân sách quá hẹp thì thà thiếu phủ (CÓ log cảnh
    ///   báo ở call site) còn hơn tắt câm cả tính năng đào sâu (KHÔNG có triệu chứng nào).</item>
    /// </list>
    ///
    /// <para><paramref name="maxQuestions"/> ≤ 0 = KHÔNG trần buổi ⇒ bỏ luôn vế ngân sách lẫn vế chừa
    /// khe (<c>AnswerService</c> không bao giờ hết ngân sách), chỉ còn config và sàn phủ.</para>
    /// </summary>
    private static int ComputeSeedCount(
        int maxQuestions, int maxDeepPerQuestion, int configuredSeedCount, int contentCriteriaCount)
    {
        var configured = Math.Max(1, configuredSeedCount);
        var floorByCriteria = Math.Max(1, contentCriteriaCount);

        if (maxQuestions <= 0)
            return Math.Max(configured, floorByCriteria);

        // ceil-div: (a + b - 1) / b với b = 1 + maxDeepPerQuestion
        var byBudget = (maxQuestions + maxDeepPerQuestion) / (1 + maxDeepPerQuestion);
        var seeds = Math.Max(Math.Min(byBudget, configured), floorByCriteria);
        return Math.Clamp(seeds, 1, Math.Max(1, maxQuestions - 1));
    }

    // SC3 — single source of truth shared by session creation and the UI-options endpoint. Preserve the
    // existing tiering branches exactly; their known MaxQuestions/adaptiveOn asymmetry is out of scope.
    /// <remarks>
    /// <paramref name="requestedMaxDeep"/> và <paramref name="requestedAdaptive"/> là lựa chọn của
    /// ỨNG VIÊN, cả hai đều <c>null</c> = "không có ý kiến". Đặt optional ở CUỐI có chủ đích: mọi call
    /// site cũ biên dịch không sửa và chạy y hệt từng byte, nên bộ test đang khoá hàm này (độ phủ
    /// seed SC1, adaptive theo tier, kill-switch INT-17b) vẫn là lưới an toàn thật chứ không phải
    /// vừa được nới ra cho vừa thay đổi mới.
    /// </remarks>
    private SessionGenerationSettings ResolveSessionSettings(
        int? questionCount, EntitlementSnapshot entitlement,
        int? requestedMaxDeep = null, bool? requestedAdaptive = null)
    {
        // ADAPTIVE Ở MỌI TIER — `Adaptive:Enabled` là SÀN, gói chỉ được CỘNG chứ không được TRỪ.
        //
        // Vì sao không còn `_tieringEnabled ? entitlement.AdaptiveEnabled : …`: một buổi tiêu đúng 1
        // credit bất kể gói (PAY-1/BC-1), nên gói không được lấy mất chính engine mà ứng viên vừa trả
        // tiền để chạy. Điều kiện OR còn bịt hai đường tắt ÂM THẦM mà nhánh cũ để hở, cả hai đều làm
        // ứng viên MẤT credit rồi nhận buổi luồng tĩnh, không lỗi, không cảnh báo:
        //   (a) Payment sập → fallback `EntitlementSnapshot.Free`;
        //   (b) admin tạo plan mới mà quên bật `AdaptiveEnabled` (DTO mặc định `false`).
        // Chiều ngược lại vẫn giữ: gói BẬT adaptive trong khi cờ rollout chung còn tắt ⇒ tier đó có
        // adaptive. Đó là lý do `Plan.AdaptiveEnabled` vẫn còn ý nghĩa thật, không phải cột chết.
        // Ứng viên CHỈ TỪ CHỐI ĐƯỢC, không tự bật được: `requestedAdaptive == false` cho ra buổi tĩnh
        // (đúng số câu đã chọn, không câu chèn), còn gửi `true` khi admin/gói đã tắt thì KHÔNG bật lên.
        // Cấu hình admin là TRẦN, không phải gợi ý — nếu không, một ô chọn trên wizard sẽ vô hiệu hoá
        // được cả kill-switch vận hành.
        var adaptiveEnabled = (_adaptive.Enabled || (_tieringEnabled && entitlement.AdaptiveEnabled))
            && (requestedAdaptive ?? true);

        // Độ sâu: ứng viên chọn thấp hơn hoặc bằng cấu hình, không bao giờ cao hơn. `Math.Clamp` ở đây
        // là LƯỚI AN TOÀN cho đường internal — đường HTTP đã bị `SelectableMaxDeepPerQuestion` chặn
        // bằng 400 từ trước reserve, vì cắt im lặng nghĩa là ứng viên trả 1 credit cho một buổi khác
        // thứ họ đã chọn (đúng bài học của `questionCount`).
        var configuredDeep = Math.Max(0, _adaptive.MaxDeepPerQuestion);
        var maxDeepPerQuestion = adaptiveEnabled
            ? Math.Clamp(requestedMaxDeep ?? configuredDeep, 0, configuredDeep)
            : 0;

        // Trần buổi: gói chỉ có tiếng nói khi cấp một con số DƯƠNG. `0` = "gói không khai trần riêng"
        // (fallback Free, hoặc plan để trống cap) ⇒ rơi về trần cấu hình. KHÔNG được để 0 chảy thẳng
        // vào `Math.Clamp(x, 0, 0)`: nó XOÁ luôn `questionCount` ứng viên đã chọn về 0.
        // ⚠ `budget == 0` (config cũng để 0 = "không trần cứng") giữ nguyên hành vi cũ: không kẹp.
        var budget = _tieringEnabled && entitlement.MaxQuestions > 0
            ? entitlement.MaxQuestions
            : _adaptive.MaxQuestions;
        var maxQuestions = adaptiveEnabled
            ? budget > 0
                ? Math.Clamp(questionCount ?? budget, 0, Math.Min(MaxQuestionCount, budget))
                : questionCount ?? 0
            : 0;

        // Cùng luật cho trần theo BUỔI (chỉ còn hiệu lực ở chế độ frontier, xem `AdaptiveOptions`).
        var maxFollowUps = maxDeepPerQuestion > 0
            ? 0
            : adaptiveEnabled
                ? _tieringEnabled && entitlement.MaxFollowUps > 0 ? entitlement.MaxFollowUps : _adaptive.MaxFollowUps
                : 0;
        return new SessionGenerationSettings(adaptiveEnabled, maxQuestions, maxFollowUps, maxDeepPerQuestion);
    }

    private sealed record SessionGenerationSettings(
        bool AdaptiveEnabled, int MaxQuestions, int MaxFollowUps, int MaxDeepPerQuestion);

    private async Task<List<QuestionTargetCriterionDto>> LoadTargetableCriteriaAsync(
        Guid candidateId, JobCategory jobCategory, string language, CancellationToken ct)
    {
        var owner = await B2CRubricScope.ResolveOwnerAsync(_db, candidateId, jobCategory, language, ct);
        var query = _db.RubricCriteria.AsNoTracking()
            .Where(c => c.IsActive
                        && c.CampaignId == null
                        && c.JobCategory == jobCategory
                        && c.Language == language
                        && c.ScoringScope == ScoringScope.WhenTargeted);
        query = owner is Guid oid
            ? query.Where(c => c.CandidateId == oid)
            : query.Where(c => c.CandidateId == null);

        return await query
            .OrderBy(c => c.Name)
            .Select(c => new QuestionTargetCriterionDto(c.Id, c.Name))
            .ToListAsync(ct);
    }

    /// <summary>
    /// Con dấu rubric B2C đóng lên buổi luyện lúc TẠO: (chủ bộ tiêu chí, phiên bản).
    ///
    /// <para>Cùng mẫu với mọi con dấu khác (<c>SelfConsistencyN</c>, <c>EntitlementSource</c>,
    /// <c>CampaignRubricVersion</c>): "dùng cấu hình LÚC TẠO, không phải cấu hình đổi sau". Ở đây
    /// "cấu hình đổi sau" có hai nguồn thật — admin lưu bản mới của bộ chuẩn, và chính ứng viên bấm
    /// Lưu rubric riêng giữa buổi.</para>
    ///
    /// <para>Trả <c>(null, null)</c> khi không tìm thấy bộ nào đang hiệu lực ⇒ buổi không ghim và
    /// loader rơi về nhánh cũ. Ca này không tới được ở đường thường (<see cref="EnsureRubricExistsAsync"/>
    /// đã chặn), nhưng ghim một phiên bản không tồn tại thì tệ hơn nhiều: nó nạp 0 tiêu chí.</para>
    /// </summary>
    private async Task<(Guid? OwnerId, int? Version)> ResolveB2CRubricPinAsync(
        Guid candidateId, JobCategory jobCategory, string language, CancellationToken ct)
    {
        var owner = await B2CRubricScope.ResolveOwnerAsync(_db, candidateId, jobCategory, language, ct);
        var query = _db.RubricCriteria.AsNoTracking()
            .Where(c => c.CampaignId == null && c.JobCategory == jobCategory
                        && c.Language == language && c.IsActive);
        query = owner is Guid oid
            ? query.Where(c => c.CandidateId == oid)
            : query.Where(c => c.CandidateId == null);

        // Bộ active của một (chủ, nghề, ngôn ngữ) luôn cùng một Version (replace-all của BC16 và của
        // màn admin đều ghi cả bộ trong MỘT SaveChanges). Max() chỉ là cách đọc con số đó an toàn khi
        // tập rỗng.
        var version = await query.Select(c => (int?)c.Version).MaxAsync(ct);
        return version is null ? (null, null) : (owner, version);
    }

    private async Task EnsureRubricExistsAsync(
        Guid candidateId, JobCategory jobCategory, string language, CancellationToken ct)
    {
        var ownerId = await B2CRubricScope.ResolveOwnerAsync(_db, candidateId, jobCategory, language, ct);
        var exists = await _db.RubricCriteria.AsNoTracking().AnyAsync(c =>
            c.CampaignId == null
            && c.CandidateId == ownerId
            && c.JobCategory == jobCategory
            && c.Language == language
            && c.IsActive,
            ct);

        if (!exists)
            throw new InvalidOperationException($"Chưa có rubric hoạt động cho {jobCategory} ({language}).");
    }

    // Mức kinh nghiệm ứng viên tự khai — đóng dấu lên session, đi vào `/decide-next` để câu đào sâu
    // hỏi đúng tầm. Tập ĐÓNG, khớp `ck_practice_sessions_seniority` ở DB **và** `RoadmapLevel`.
    private static readonly string[] AllowedSeniorities = ["Fresher", "Junior", "Middle", "Senior"];
    private const string DefaultSeniority = "Junior";

    /// <summary>
    /// Hợp đồng seniority — CHUNG cho B2C và B2B (worker Campaign áp cùng luật, đừng chế biến thể):
    /// <list type="bullet">
    ///   <item><c>null</c> (client cũ không gửi) → <c>"Junior"</c>.</item>
    ///   <item>chuỗi RỖNG sau <c>Trim()</c> → <b>400</b>, KHÔNG âm thầm reset về Junior: client gửi
    ///   <c>""</c> là đang gửi một giá trị SAI, khác hẳn với không gửi gì.</item>
    ///   <item>không thuộc tập (so <b>case-sensitive</b>, khớp CHECK ở DB) → 400.</item>
    ///   <item>hợp lệ → giá trị đã trim.</item>
    /// </list>
    /// ⚠ <c>InvalidOperationException</c> chứ KHÔNG phải <c>ArgumentException</c>: PracticeController
    /// chỉ bắt loại đầu → 400; loại sau rơi xuống <c>catch(Exception)</c> → 500 (cùng bẫy với
    /// <see cref="ValidateTimeLimitSec"/>). Guard chạy TRƯỚC reserve ⇒ input sai không giữ credit (PAY-5).
    /// </summary>
    private static string ValidateSeniority(string? requested)
    {
        if (requested is null) return DefaultSeniority;
        var seniority = requested.Trim();
        if (!AllowedSeniorities.Contains(seniority, StringComparer.Ordinal))
            throw new InvalidOperationException(
                $"seniority chỉ nhận {string.Join(" / ", AllowedSeniorities)} (đang gửi: '{requested}').");
        return seniority;
    }

    private static int ValidateTimeLimitSec(int? requested)
    {
        if (requested is null) return DefaultTimeLimitSec;
        if (!AllowedTimeLimitsSec.Contains(requested.Value))
            throw new InvalidOperationException(
                $"timeLimitSec chỉ nhận {string.Join(" / ", AllowedTimeLimitsSec)} giây (đang gửi: {requested.Value}).");
        return requested.Value;
    }

    // F2b — trần số câu.
    //
    // VÌ SAO PHẢI CÓ TRẦN: chi phí tăng TUYẾN TÍNH theo số câu (mỗi câu = 1 lượt Whisper + N lần gọi
    // Gemini do self-consistency + 1 lần TTS gần như luôn miss cache) nhưng doanh thu là HẰNG SỐ 1
    // credit/buổi — ReserveAsync gọi đúng một lần lúc tạo session, không scale theo số câu. Không có
    // trần thì một người chọn 500 câu vừa ăn hết biên credit-to-cost vừa làm nghẽn queue chấm của
    // mọi người khác (Whisper chạy CPU, xử lý tuần tự).
    private const int MinQuestionCount = 1;
    private const int MaxQuestionCount = 20;

    // Số câu AIService tự sinh khi .NET không truyền `count`. Trần cứng ở trên là của HỆ THỐNG; con số
    // này là mặc định của AIService — hai thứ khác nhau, đừng gộp.
    private const int AiServiceDefaultQuestionCount = 5;

    /// <summary>
    /// Trần số câu THẬT của một ứng viên = min(trần hệ thống, trần gói). Dùng CHUNG bởi
    /// <see cref="GetSessionOptionsAsync"/> (số báo cho UI) và <see cref="CreateSessionInternalAsync"/>
    /// (số dùng để từ chối). Hai chỗ tính khác nhau chính là bug: endpoint báo max 8 mà POST
    /// <c>questionCount: 20</c> vẫn 200 rồi bị <c>Math.Clamp</c> trong <see cref="ResolveSessionSettings"/>
    /// cắt còn 8 trong im lặng — ứng viên trả 1 credit cho một buổi khác thứ họ đã chọn.
    /// </summary>
    private int EffectiveMaxQuestionCount(EntitlementSnapshot entitlement)
    {
        var baseline = ResolveSessionSettings(null, entitlement);
        return baseline.MaxQuestions is > 0 and <= MaxQuestionCount
            ? baseline.MaxQuestions
            : MaxQuestionCount;
    }

    /// <summary>
    /// Trần TUYỆT ĐỐI của hệ thống cho độ sâu ứng viên B2C được chọn — khớp đúng
    /// <c>CampaignService.MaxDeepPerQuestionCap</c> của B2B, để hai dòng sản phẩm không lệch thang.
    ///
    /// <para>Vì sao có hằng này bên cạnh <c>Adaptive:MaxDeepPerQuestion</c>: admin nâng cấu hình lên 5
    /// là quyết định VẬN HÀNH (đổi hình dạng buổi cho toàn hệ), không phải quyết định mở thêm hai mức
    /// cho ứng viên tự chọn — wizard cũng chỉ có 3 nhãn. Trần thật = min(hằng này, cấu hình).</para>
    /// </summary>
    private const int MaxDeepPerQuestionCap = 3;

    // null = client không chọn → trả null để KHÔNG ghi đè mặc định của AIService (giữ hành vi cũ = 5 câu).
    //
    // ⚠ Đây là trần TUYỆT ĐỐI của hệ thống, kiểm sớm và rẻ (không cần entitlement). Trần theo GÓI hẹp
    // hơn được kiểm riêng sau khi resolve entitlement — xem EffectiveMaxQuestionCount.
    private static int? ValidateQuestionCount(int? requested)
    {
        if (requested is null) return null;
        if (requested.Value is < MinQuestionCount or > MaxQuestionCount)
            throw new InvalidOperationException(
                $"questionCount phải nằm trong khoảng {MinQuestionCount}..{MaxQuestionCount} (đang gửi: {requested.Value}).");
        return requested.Value;
    }

    /// <summary>
    /// Độ sâu ứng viên chọn — <c>null</c> = không chọn (giữ mặc định server); ngoài <c>1..3</c> → 400.
    ///
    /// <para>⚠ Miền bắt đầu từ <b>1</b>, KHÔNG phải 0. <c>0</c> không có nghĩa "tắt đào sâu" mà là
    /// BỘ CHỌN CHẾ ĐỘ ENGINE (frontier cũ — vẫn chèn câu, chỉ dồn ở đuôi buổi) và nó lật cả nghĩa
    /// của <c>MaxFollowUps</c> trong <see cref="ResolveSessionSettings"/>. Nhận 0 từ client là để ứng
    /// viên đổi thuật toán của buổi thi bằng một ô chọn mà không quyết định sản phẩm nào phủ. Muốn
    /// buổi tĩnh thì gửi <c>adaptiveEnabled: false</c>.</para>
    ///
    /// <para>Đây là trần tuyệt đối, kiểm sớm và rẻ (chưa cần entitlement) — cùng khuôn
    /// <see cref="ValidateQuestionCount"/>. Trần THẬT theo cấu hình được kiểm riêng sau khi resolve
    /// entitlement, xem <see cref="SelectableMaxDeepPerQuestion"/>.</para>
    /// </summary>
    private static int? ValidateMaxDeepPerQuestion(int? requested)
    {
        if (requested is null) return null;
        if (requested.Value is < 1 or > MaxDeepPerQuestionCap)
            throw new InvalidOperationException(
                $"maxDeepPerQuestion phải nằm trong khoảng 1..{MaxDeepPerQuestionCap} (đang gửi: {requested.Value}).");
        return requested.Value;
    }

    /// <summary>
    /// Trần độ sâu THẬT của một người dùng = <c>min(</c><see cref="MaxDeepPerQuestionCap"/><c>, cấu hình đã resolve)</c>.
    ///
    /// <para>Một hàm duy nhất cho CẢ số báo cho UI (<see cref="GetSessionOptionsAsync"/>) lẫn số dùng
    /// để từ chối (<see cref="CreateSessionInternalAsync"/>) — cùng lý do với
    /// <see cref="EffectiveMaxQuestionCount"/>: hai chỗ tính khác nhau CHÍNH LÀ bug, và repo đã dính
    /// đúng lớp đó một lần với <c>questionCount</c>.</para>
    ///
    /// <para>Trả <c>0</c> khi adaptive tắt hoặc cấu hình để 0 ⇒ "không có gì để chọn" ⇒ UI ẩn ô.</para>
    /// </summary>
    private int SelectableMaxDeepPerQuestion(EntitlementSnapshot entitlement)
        => Math.Min(MaxDeepPerQuestionCap, ResolveSessionSettings(null, entitlement).MaxDeepPerQuestion);

    /// <summary>
    /// INT-17b — khoảng cách <c>OrderNo</c> giữa hai câu GỐC liền nhau, để chuỗi đào sâu của câu trước
    /// có chỗ nằm xen vào mà không đụng câu sau.
    ///
    /// Câu gốc thứ <c>i</c> nhận <c>i × stride + 1</c> (maxDeep=3 → 1, 5, 9, 13, 17); câu đào sâu nhận
    /// <c>OrderNo của câu cha + 1</c> (2, 3, 4 …). Sắp theo <c>OrderNo</c> là ra đúng thứ tự hội thoại.
    ///
    /// VÌ SAO ĐÁNH SỐ CÓ KHOẢNG TRỐNG thay vì thêm field <c>displayOrder</c>: <c>MapToResponse</c> đã
    /// sắp theo <c>OrderNo</c>, FE B2B tự sắp lại cũng theo <c>OrderNo</c>, FE B2C dùng thẳng thứ tự
    /// mảng BE trả — và HAI MÀN ỨNG VIÊN không hiển thị <c>OrderNo</c> (đều đánh số câu theo chỉ số
    /// mảng: practice-session.html:28 "Câu i + 1", campaign-interview.html:47 "Câu currentIndex() + 1").
    /// Nên cách này cho thứ tự đúng ở hai màn đó mà không phải đổi DTO lẫn FE.
    ///
    /// ⚠ NGOẠI LỆ, CHƯA SỬA — màn transcript của Employer CÓ hiển thị <c>OrderNo</c> thô:
    /// session-transcript-dialog.ts:78 render "Câu q.orderNo", dữ liệu đi từ <c>QuestionResponse.OrderNo</c>
    /// qua <c>CampaignResultsDtos.cs:101</c> ⇒ với stride 4 HR thấy "Câu 1, 2, 5, 9, 13". Ghi nhận ở đây
    /// để lần sau đọc khối này không tưởng là đã phủ hết.
    /// Đánh số LẠI (renumber) thì không được: unique <c>(session_id, order_no)</c> là INDEX chứ không
    /// phải constraint nên không hoãn (DEFERRABLE) được, mọi phép dịch số sẽ đụng nhau giữa chừng.
    ///
    /// <c>maxDeep = 0</c> (chế độ cũ) → stride 1 ⇒ câu gốc đánh 1,2,3… y như trước.
    /// </summary>
    private static int SeedOrderStride(int maxDeepPerQuestion) => 1 + Math.Max(0, maxDeepPerQuestion);

    /// <summary>
    /// F2b — kẹp trần câu thích ứng của B2B về đúng miền CHECK ở DB (0..20).
    ///
    /// VÌ SAO KẸP CHỨ KHÔNG NÉM: nếu để nguyên một giá trị ngoài miền thì CHECK
    /// `ck_practice_sessions_max_questions_range` nổ ngay lúc INSERT — tức là ứng viên bấm "Bắt đầu"
    /// và nhận lỗi, SAU KHI credit org đã bị reserve. Đổi một cấu hình sai của HR lấy một buổi thi
    /// hỏng là đánh đổi tệ; kẹp + log để HR sửa cấu hình mà ứng viên vẫn thi được.
    ///
    /// ✅ Lỗ upstream đã vá (INT-17b): `CampaignService.ValidateAdaptiveCaps` nay có đủ trần TRÊN cho
    /// cả ba (`MaxQuestionsPerSession`, `MaxFollowUpsCap`, `MaxDeepPerQuestionCap`), không còn cảnh
    /// "chỉ chặn số âm, HR gõ 100000 là qua sạch". Giữ chỗ kẹp này làm **lưới an toàn** cho đường
    /// internal (Campaign gọi thẳng, không đi qua validate) chứ không phải bản vá thay thế.
    /// </summary>
    private int ClampCampaignMaxQuestions(int? requested, Guid campaignId)
    {
        var value = requested ?? 0;
        if (value >= 0 && value <= MaxQuestionCount) return value;

        var clamped = Math.Clamp(value, 0, MaxQuestionCount);
        _logger.LogWarning(
            "Campaign {CampaignId} cấu hình max_questions={Requested} ngoài miền 0..{Max} → kẹp về {Clamped}",
            campaignId, value, MaxQuestionCount, clamped);
        return clamped;
    }

    private static PracticeSessionResponse MapToResponse(
        PracticeSession s, List<PracticeQuestion> questions, List<PracticeAnswer> answers,
        IReadOnlyList<SessionCriterionScore>? criterionScores = null,
        IReadOnlyList<string>? cvStrengths = null,
        BenchmarkResponse? benchmark = null,   // F14
        IReadOnlyList<CriterionEvidenceResponse>? criterionEvidence = null)
    {
        var answerByQuestion = answers.ToDictionary(a => a.QuestionId);

        var qResponses = questions
            .OrderBy(q => q.OrderNo)
            .Select(q => new QuestionResponse(
                q.Id, q.OrderNo, q.Content, q.TimeLimitSec,
                answerByQuestion.TryGetValue(q.Id, out var a) ? MapAnswer(s.Id, a) : null,
                q.Kind.ToString(),   // phỏng vấn THÍCH ỨNG — Seed | FollowUp | Clarify | NewQuestion
                q.GroundingRefs))    // RAG grounding — null (không grounding) / [] (ungrounded) / non-empty (grounded)
            .ToList();

        return new PracticeSessionResponse(
            s.Id, s.Status.ToString(), s.JobCategory.ToString(),
            s.Language,
            s.CvId, s.JdId, s.CreatedAt, s.CompletedAt, qResponses,
            MapResult(s, questions.Count, criterionScores, cvStrengths, benchmark),
            s.Seniority,
            criterionEvidence is { Count: > 0 } ? criterionEvidence : null);
    }

    // BC9: dựng tổng kết buổi từ DB. Chỉ trả khi B2C đã Scored & có breakdown; ngược lại null.
    private static SessionResultResponse? MapResult(
        PracticeSession s, int totalQuestions, IReadOnlyList<SessionCriterionScore>? criterionScores,
        IReadOnlyList<string>? cvStrengths = null,
        BenchmarkResponse? benchmark = null)   // F14
    {
        if (s.Status != SessionStatus.Scored || s.CampaignId is not null
            || criterionScores is not { Count: > 0 })
            return null;

        var criteria = criterionScores
            .Select(cs => new CriterionScoreResponse(
                cs.CriterionId, cs.CriterionName, cs.AverageScore, cs.MaxScore, cs.Percentage, cs.Weight))
            .ToList();

        var needsImprovement = criterionScores
            .Where(cs => cs.NeedsImprovement)
            .Select(cs => cs.CriterionId)
            .ToList();

        // BC8: mục "CV vs câu trả lời" — null nếu buổi không có CV đã phân tích (cvStrengths rỗng).
        var cvVsAnswer = CvVsAnswerReportBuilder.Build(cvStrengths ?? Array.Empty<string>(), criterionScores);

        return new SessionResultResponse(
            s.OverallScore ?? 0m,
            s.AnsweredCount ?? 0,
            totalQuestions,
            criteria,
            needsImprovement,
            OverallComment: s.OverallComment,   // BC10 — nhận xét chung (AI, best-effort); null nếu chưa/AI lỗi.
            CvVsAnswer: cvVsAnswer,
            Benchmark: benchmark,   // F14 — mốc đối chiếu; null khi tắt / caller không dựng
            // Nguồn thước đo — đọc THẲNG con dấu của buổi, không tra lại trạng thái hôm nay (tra lại
            // là gắn nhãn sai cho buổi cũ của người vừa lưu rubric riêng). `null` = KHÔNG BIẾT, và
            // KHÔNG được suy thành "bộ chuẩn" (BK23: đừng suy "biết" từ "không biết").
            RubricSource: s.B2CRubricVersion is null
                ? null
                : s.B2CRubricOwnerId is null ? "SystemDefault" : "Custom",
            RubricVersion: s.B2CRubricVersion);
    }

    // BC8: gộp tín hiệu "CV mạnh" = strengths + matched skills (nếu có JD match), khử trùng giữ thứ tự.
    private static IReadOnlyList<string> MergeStrengths(CvAnalysis cv)
    {
        var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        var merged = new List<string>();
        foreach (var s in cv.Strengths.Concat(cv.JdMatch?.MatchedSkills ?? Enumerable.Empty<string>()))
        {
            var v = s?.Trim();
            if (string.IsNullOrEmpty(v)) continue;
            if (seen.Add(v)) merged.Add(v);
        }
        return merged;
    }

    private static AnswerResponse MapAnswer(Guid sessionId, PracticeAnswer a)
    {
        // E10 — mỗi tiêu chí: điểm chốt = MEDIAN qua các attempt (self-consistency); reasoning/level
        // lấy từ attempt ĐẠI DIỆN (điểm gần median nhất, tie-break attempt mới nhất) để nhận xét khớp
        // điểm hiển thị. N=1 → median = giá trị attempt đó, đại diện = chính nó → giữ hiển thị cũ.
        var perCriterion = a.Scores
            .GroupBy(sc => sc.CriterionId)
            .Select(g =>
            {
                var median = ScoreStatistics.Median(g.Select(s => s.Score));
                var rep = g.OrderBy(s => Math.Abs(s.Score - median))
                           .ThenByDescending(s => s.AttemptNo)
                           .First();
                // Criterion nạp qua .ThenInclude ở các site đọc; dùng `?.` để site nào lỡ quên Include
                // thì ra null (client lùi về nhãn chung) thay vì ném NRE giữa luồng xem kết quả.
                return new AnswerScoreResponse(
                    g.Key, median, rep.Reasoning, rep.RubricVersion, rep.LevelMatched,
                    rep.Criterion?.Name, rep.Criterion?.MaxScore);
            })
            .ToList();

        return new AnswerResponse(
            a.Id, a.Status.ToString(), a.DurationSec, a.Transcript, perCriterion, a.NeedsReview,
            a.SampleAnswer,   // F13 — gợi ý câu trả lời mẫu (null khi chưa chấm / LLM không trả)
            DeliveryMetricsMapper.Read(a),   // F11 — chỉ số trôi chảy (null khi chưa đo được)
            string.IsNullOrWhiteSpace(a.AudioObjectKey)
                ? null
                : $"/api/v1/interview/practice/sessions/{sessionId}/answers/{a.Id}/audio");
    }

    private async Task ConsumeQuietlyAsync(Guid sessionId, CancellationToken ct)
    {
        try
        {
            await _reservationClient.ConsumeAsync(sessionId, ct);
            _logger.LogInformation(
                "PONR1: consume 1 credit cho session {SessionId} tại mốc sinh câu hỏi", sessionId);
        }
        catch (Exception ex)
        {
            _logger.LogError(ex,
                "PONR1: consume credit lỗi cho session {SessionId} sau khi đã Ready/materialize → " +
                "reservation còn Reserved, chờ lưới cuối E7/R1 hoàn tất thu (KHÔNG release)", sessionId);
        }
    }
}
