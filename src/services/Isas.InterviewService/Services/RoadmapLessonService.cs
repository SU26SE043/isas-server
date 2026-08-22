using Isas.InterviewService.ApplicationDbContext;
using Isas.InterviewService.DTOs;
using Isas.InterviewService.Entities;
using Isas.InterviewService.Enums;
using Isas.InterviewService.Models;
using Isas.InterviewService.Services.Interfaces;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Options;

namespace Isas.InterviewService.Services;

// BC14 (D20) — thao tác cấp lesson: mở lesson (lý thuyết lazy) + /start luyện. Owner-only.
// Lý thuyết sinh LẦN ĐẦU (lazy, idempotent) — miễn phí. /start = practice session B2C (reserve credit,
// tái dùng PracticeService.CreateLessonSessionAsync) rồi link lesson. Scored→Done / Abandoned→Theory
// móc ở luồng đóng session (SessionScoringNotifier / SessionAbandonSweeper) — KHÔNG ở đây.
public class RoadmapLessonService : IRoadmapLessonService
{
    private readonly InterviewDbContext _db;
    private readonly IPracticeService _practiceService;
    private readonly IAiServiceRoadmapGenerator _generator;
    private readonly ILogger<RoadmapLessonService> _logger;

    private readonly ScoringOptions _scoring;   // F6a — ngưỡng "điểm yếu" (dùng chung với BC9)

    public RoadmapLessonService(
        InterviewDbContext db,
        IPracticeService practiceService,
        IAiServiceRoadmapGenerator generator,
        ILogger<RoadmapLessonService> logger,
        // Optional (default null) → test cũ dựng 4 tham số vẫn compile; DI inject bản thật.
        IOptions<ScoringOptions>? scoringOptions = null)
    {
        _db = db;
        _practiceService = practiceService;
        _generator = generator;
        _logger = logger;
        _scoring = scoringOptions?.Value ?? new ScoringOptions();
    }

    public async Task<LessonResponse> OpenLessonAsync(
        Guid candidateId, Guid roadmapId, Guid lessonId, CancellationToken ct = default)
    {
        var lesson = await LoadOwnedLessonAsync(candidateId, roadmapId, lessonId, ct);
        var roadmap = lesson.Milestone.Roadmap;

        // Số lần đã làm bài này — đọc 1 lần, dùng cho mọi nhánh trả về bên dưới (trạng thái lesson
        // không đổi trong lời gọi này: đường sinh lý thuyết không chạm `status`).
        var attemptCount = await _db.RoadmapLessonAttempts
            .CountAsync(a => a.LessonId == lessonId, ct);

        // Đã có lý thuyết DÙNG ĐƯỢC → đọc DB, KHÔNG gọi AI lần 2 (lazy, idempotent).
        if (HasUsableTheory(lesson.TheoryContent))
            return MapLesson(lesson, attemptCount);

        // Lazy-gen: gọi AIService (sync). Lỗi → AiServiceException (502) → chưa lưu gì (mở lại được).
        // RAG grounding (Cách 2) — feed snapshot precompute (lesson.GroundingRefs) → AI cite trong tập đó.
        var focus = lesson.Milestone.FocusCriteria ?? new List<string>();
        var weakCriteria = FilterWeakCriteria(roadmap, focus);
        var weaknesses = FormatWeaknesses(weakCriteria);
        // BE-5 — cùng bằng chứng như lúc TẠO roadmap (RoadmapEvidenceLoader), tính lại từ
        // `roadmap.SourceSessionIds` (persisted, không cần migration để lưu riêng cho lesson).
        var evidence = weakCriteria.Count > 0 && roadmap.SourceSessionIds is { Count: > 0 }
            ? await RoadmapEvidenceLoader.LoadAsync(_db, roadmap.SourceSessionIds, weakCriteria, ct)
            : [];
        // Chế độ ôn tập phải theo lộ trình xuống TỚI bài giảng — đây mới là chỗ người học đọc nội
        // dung. Chỉ đổi cấu trúc roadmap mà để lý thuyết y như cũ thì tính năng chỉ đổi được tiêu
        // đề bài, còn thứ họ thật sự học vẫn là bài của chế độ tiến-lên.
        var generated = roadmap.Language == "vi"
            ? await _generator.GenerateLessonTheoryAsync(roadmap.JobCategory.ToString(), roadmap.Level.ToString(),
                lesson.Title, focus, weaknesses, lesson.GroundingRefs, evidence, roadmap.Mode, ct)
            : await _generator.GenerateLessonTheoryAsync(roadmap.JobCategory.ToString(), roadmap.Level.ToString(),
                lesson.Title, focus, weaknesses, lesson.GroundingRefs, ct, roadmap.Language, evidence, roadmap.Mode);
        var theory = generated.TheoryMarkdown;
        // F15 — tài liệu học sinh CÙNG lượt với lý thuyết; lưu chung 1 lần ghi để không có trạng
        // thái "có theory mà chưa có resources" (guard idempotent bên dưới chỉ nhìn theory_content).
        var resources = generated.Resources.ToList();
        var now = DateTime.UtcNow;

        // RAG grounding — NARROW snapshot precompute về đúng chunk AI THẬT SỰ cite (guard over-attribution +
        // drop by-construction: .Where trên lesson.GroundingRefs ⇒ chỉ giữ chunk vừa nằm trong tập cấp vừa
        // được cite; id lạ AI bịa tự rơi). 3 trạng thái: precompute chưa chạy (null) → null; đã chạy nhưng
        // AI không cite / corpus rỗng → [] (ungrounded); có cite → non-empty (grounded).
        var citedRefs = NarrowToCited(lesson.GroundingRefs, generated.CitedChunkIds);

        // Lưu idempotent: chỉ ghi khi CHƯA có bài dùng được. Vị ngữ phải khớp `HasUsableTheory` ở
        // nhánh đọc phía trên — lệch nhau thì bài hỏng gọi AI mỗi lần mở nhưng không bao giờ ghi
        // được, đốt token trong im lặng.
        // ⚠ Với bài hỏng cũ, 2 request đồng thời nay đều ghi được (điều kiện không còn là `== null`)
        // → bản sau đè bản trước. Cả hai đều là bài đã qua rubric nên vô hại; không thêm khoá.
        var updated = await _db.RoadmapLessons
            .Where(l => l.Id == lessonId
                        && (l.TheoryContent == null
                            || !(l.TheoryContent.Contains("\n") || l.TheoryContent.Contains("## "))))
            .ExecuteUpdateAsync(u => u
                .SetProperty(l => l.TheoryContent, theory)
                .SetProperty(l => l.Resources, resources)
                .SetProperty(l => l.GroundingRefs, citedRefs)
                .SetProperty(l => l.TheoryGeneratedAt, now), ct);

        if (updated == 0)
        {
            // Request khác vừa sinh xong trước → trả bản đã lưu (không ghi đè).
            var fresh = await _db.RoadmapLessons.AsNoTracking().FirstAsync(l => l.Id == lessonId, ct);
            return MapLesson(fresh, attemptCount);
        }

        _logger.LogInformation("BC14: sinh lý thuyết lesson {LessonId} (roadmap {RoadmapId})", lessonId, roadmapId);

        // Trả bản vừa sinh (khỏi round-trip). lesson đang detached (AsNoTracking) → set để dựng response.
        lesson.TheoryContent = theory;
        lesson.Resources = resources;
        lesson.GroundingRefs = citedRefs;
        lesson.TheoryGeneratedAt = now;
        return MapLesson(lesson, attemptCount);
    }

    /// <summary>
    /// Bài lưu trong DB có dùng được không. Bài sinh TRƯỚC bản vá này là markdown thô, không còn cấu
    /// trúc để chấm lại bằng rubric của AIService — nên ở đây chỉ nhận ca RÕ RÀNG hỏng: nội dung vỏn
    /// vẹn một dòng tiêu đề, không có gì bên dưới. Đúng bài 51 ký tự gặp trên deploy 2026-08-03; bài
    /// như vậy trước đây đóng đinh vĩnh viễn, người học không có đường nào lấy lại nội dung.
    ///
    /// "Có gì đó ngoài tiêu đề" = có xuống dòng HOẶC có mục con <c>"## "</c>. Vế thứ hai không thừa:
    /// preflight trên DB thật (2026-08-04) có 5 bài không chứa ký tự xuống dòng nào, nhưng MỘT trong
    /// số đó dài 7.904 ký tự và có nhiều mục <c>##</c> nằm cùng dòng — nội dung thật, chỉ trình bày
    /// liền dòng. Thiếu vế này thì nó bị coi là bài trắng và bị sinh đè, tức lấy một bài có nội dung
    /// đổi lấy một canh bạc.
    ///
    /// CỐ Ý không đặt ngưỡng số ký tự: chất lượng do rubric AIService định nghĩa (đủ phần, không phải
    /// đủ dài), và một ngưỡng .NET chặt hơn rubric đó sẽ khiến bài vừa được chấp nhận bị coi là hỏng
    /// rồi sinh lại MỖI LẦN MỞ, vô hạn.
    ///
    /// Vị ngữ này phải khớp điều kiện <c>.Where(...)</c> lúc ghi trong <see cref="OpenLessonAsync"/>.
    /// </summary>
    private static bool HasUsableTheory(string? theory) =>
        !string.IsNullOrEmpty(theory) && (theory.Contains('\n') || theory.Contains("## "));

    // RAG grounding — narrow snapshot precompute về đúng chunk được cite. null (chưa precompute) → null;
    // đã precompute nhưng không cite → [] (ungrounded); có cite → subset. .Where trên tập cấp ⇒ id lạ tự rơi.
    private static List<GroundingChunk>? NarrowToCited(
        IReadOnlyList<GroundingChunk>? provided, IReadOnlyList<string>? citedChunkIds)
    {
        if (provided is null) return null;
        if (citedChunkIds is not { Count: > 0 }) return new List<GroundingChunk>();
        var cited = new HashSet<string>(citedChunkIds, StringComparer.Ordinal);
        return provided.Where(g => cited.Contains(g.ChunkId)).ToList();
    }

    public async Task<PracticeSessionResponse> StartLessonAsync(
        Guid candidateId, Guid roadmapId, Guid lessonId, CancellationToken ct = default)
    {
        var lesson = await LoadOwnedLessonAsync(candidateId, roadmapId, lessonId, ct);

        // Đang luyện / đã xong → 409 (resume session cũ, KHÔNG reserve thêm credit).
        // ⚠ Ca `Done` KHÔNG được nới thành "tạo buổi mới" ở đây: làm lại đi qua endpoint RIÊNG
        // (`RetryLessonAsync`) để FE phân biệt được "tiếp tục buổi dở" với "tạo buổi mới" — nhét cả
        // hai vào một route thì phân biệt bằng trạng thái ngầm, về sau không ai đọc ra được.
        if (lesson.Status == LessonStatus.Practicing)
            throw new LessonAlreadyStartedException("Lesson đang luyện — tiếp tục buổi hiện tại.", lesson.SessionId);
        if (lesson.Status == LessonStatus.Done)
            throw new LessonAlreadyStartedException("Lesson đã hoàn thành.", lesson.SessionId);

        return await BeginSessionAsync(candidateId, lesson, LessonStatus.Theory, retry: false, ct);
    }

    /// <summary>
    /// Làm lại một bài ĐÃ hoàn thành để nâng điểm. Tốn ĐÚNG 1 credit như mọi buổi luyện khác (đi qua
    /// cùng đường reserve, không có nhánh miễn phí — nó là một buổi phỏng vấn thật) và câu hỏi được
    /// SINH MỚI (tái dùng nguyên <c>CreateLessonSessionAsync</c>, không chép câu cũ).
    ///
    /// <para>Tiền điều kiện NGƯỢC với <see cref="StartLessonAsync"/>: chỉ <c>Done</c> mới làm lại
    /// được. Còn <c>Theory</c> → 409 (chưa học lần nào thì bấm Bắt đầu); đang <c>Practicing</c> →
    /// 409 (tiếp tục buổi dở, không mở buổi thứ hai).</para>
    ///
    /// <para><b>Lộ trình đã đóng thì MỞ LẠI:</b> roadmap <c>Completed</c> → về <c>Active</c> và xoá
    /// bản báo cáo chốt sổ. Không làm vậy thì người học nâng điểm xong mà báo cáo vẫn là bản cũ ⇒
    /// nút bấm thành vô nghĩa. Khi bài đó xong lần nữa, BC15 tự đóng sổ lại với số MỚI.</para>
    /// </summary>
    public async Task<PracticeSessionResponse> RetryLessonAsync(
        Guid candidateId, Guid roadmapId, Guid lessonId, CancellationToken ct = default)
    {
        var lesson = await LoadOwnedLessonAsync(candidateId, roadmapId, lessonId, ct);

        if (lesson.Status == LessonStatus.Theory)
            throw new LessonRetryNotAllowedException(
                "Lesson chưa được luyện lần nào — hãy bấm Bắt đầu.", null);
        if (lesson.Status == LessonStatus.Practicing)
            throw new LessonRetryNotAllowedException(
                "Lesson đang luyện — tiếp tục buổi hiện tại.", lesson.SessionId);

        return await BeginSessionAsync(candidateId, lesson, LessonStatus.Done, retry: true, ct);
    }

    /// <summary>
    /// Thân chung của "Bắt đầu" và "Làm lại": reserve credit → tạo buổi → lật trạng thái lesson ATOMIC.
    ///
    /// <paramref name="expectedStatus"/> là trạng thái mà lesson PHẢI còn đang ở khi cú lật xảy ra —
    /// đây là thứ DUY NHẤT chặn hai request đồng thời mở hai buổi cho cùng một bài. Không được bỏ.
    /// </summary>
    private async Task<PracticeSessionResponse> BeginSessionAsync(
        Guid candidateId, RoadmapLesson lesson, LessonStatus expectedStatus, bool retry, CancellationToken ct)
    {
        var roadmap = lesson.Milestone.Roadmap;
        var lessonId = lesson.Id;
        var roadmapId = roadmap.Id;

        // Practice session B2C: reserve 1 credit (hết → 402 KHÔNG tạo session), câu hỏi bám focusCriteria.
        // sessionId cấp trước để link lesson SAU khi session tồn tại (thoả FK roadmap_lessons.session_id).
        // Reserve/gen lỗi → CreateLessonSessionAsync ném (402/gen-fail) TRƯỚC khi link ⇒ lesson vẫn Theory.
        var sessionId = Guid.NewGuid();
        // Seniority lấy từ ĐÚNG mức của roadmap, không để rơi vào mặc định "Junior".
        //
        // `RoadmapLevel` và tập seniority của session trùng khít 4 giá trị (Fresher/Junior/Middle/
        // Senior) nên `.ToString()` là ánh xạ đúng, không phải xấp xỉ. Trước đây call site này dựng
        // request POSITIONAL 3 tham số ⇒ nhận default ⇒ MỌI buổi luyện theo lộ trình đóng dấu "Junior"
        // vĩnh viễn, kể cả roadmap Senior. Không vô hại: seniority đi vào `/decide-next` (câu đào sâu
        // hỏi sai tầm) và lộ ra `PracticeSessionResponse.Seniority` cho FE.
        //
        // Language: CÙNG LỚP LỖI vừa sửa ở trên, và chưa đổ máu chỉ vì tình cờ — cả 8 buổi luyện
        // hiện có trên production đều bắt nguồn từ roadmap tiếng Việt, nhưng production ĐÃ CÓ 1
        // roadmap tiếng Anh chưa ai bấm Bắt đầu. Thiếu dòng này → request rơi về default `null` →
        // `ValidateLanguage` hạ mọi buổi luyện của roadmap English xuống "vi": câu hỏi/chấm/nhận xét
        // sai ngôn ngữ hoàn toàn, trong khi người học đã trả credit cho đúng buổi đó.
        var req = new CreatePracticeSessionRequest(
            roadmap.CvId, JdId: null, roadmap.JobCategory,
            Language: roadmap.Language, Seniority: roadmap.Level.ToString());
        var response = await _practiceService.CreateLessonSessionAsync(
            candidateId, req, sessionId, lesson.Milestone.FocusCriteria, ct);

        // Link atomic (guard Status == expectedStatus chống double-start): chỉ khi lesson CÒN đang ở
        // đúng trạng thái tiền điều kiện mới set Practicing + session_id. Đua 2 request cùng lúc →
        // chỉ 1 thắng; kẻ thua để lại session mồ côi (rất hiếm, cùng 1 user) — credit sẽ được E7 hoàn
        // khi session đó bỏ ngang/hết hạn.
        //
        // ⚠ `session_id` bị GHI ĐÈ ở lần làm lại — CÓ CHỦ ĐÍCH: cột đó mang nghĩa "buổi MỚI NHẤT" và
        // mọi chỗ đang đọc nó (BC15 rollup, improvement, FE) muốn đúng nghĩa đó. Lịch sử KHÔNG mất:
        // buổi cũ nằm trong `roadmap_lesson_attempts` (ghi ngay bên dưới).
        var linked = await _db.RoadmapLessons
            .Where(l => l.Id == lessonId && l.Status == expectedStatus)
            .ExecuteUpdateAsync(u => u
                .SetProperty(l => l.Status, LessonStatus.Practicing)
                .SetProperty(l => l.SessionId, sessionId), ct);

        if (linked == 0)
        {
            _logger.LogWarning(
                "BC14: lesson {LessonId} bị mở đồng thời — session {SessionId} không link được (mồ côi)",
                lessonId, sessionId);
            if (retry)
                throw new LessonRetryNotAllowedException("Lesson vừa được mở ở một yêu cầu khác.", null);
            throw new LessonAlreadyStartedException("Lesson vừa được bắt đầu ở một yêu cầu khác.", null);
        }

        // Ghi lại LẦN LÀM này. Số thứ tự cấp bằng `count + 1` — an toàn vì chỉ request vừa THẮNG cú
        // lật trạng thái ở trên mới tới được đây, và một lesson chỉ lật được một lần cho tới khi nó
        // quay về Done. UNIQUE(lesson_id, attempt_no) là lá chắn nếu giả định đó sai.
        var attemptNo = await _db.RoadmapLessonAttempts
            .CountAsync(a => a.LessonId == lessonId, ct) + 1;
        _db.RoadmapLessonAttempts.Add(new RoadmapLessonAttempt
        {
            LessonId = lessonId,
            SessionId = sessionId,
            AttemptNo = attemptNo,
            CreatedAt = DateTime.UtcNow
        });
        await _db.SaveChangesAsync(ct);

        // Milestone Pending→InProgress khi lesson đầu tiên của mile được /start (idempotent — lesson kế no-op).
        //
        // ⚠ Guard `== Pending` cũng chính là thứ giữ cho việc LÀM LẠI không hạ cấp một milestone đã
        // `Completed`: cái đã hoàn thành thì vẫn hoàn thành, người học chỉ đang cải thiện điểm.
        await _db.RoadmapMilestones
            .Where(m => m.Id == lesson.MilestoneId && m.Status == MilestoneStatus.Pending)
            .ExecuteUpdateAsync(u => u.SetProperty(m => m.Status, MilestoneStatus.InProgress), ct);

        // Làm lại trên một lộ trình ĐÃ ĐÓNG → mở lại. Xoá đúng 4 thứ mà BC15 đặt lúc đóng sổ
        // (status/final_report/overall_comment/completed_at) — để sót thứ nào thì lộ trình mang một
        // nửa trạng thái "đã xong", và `GET /report` sẽ đọc snapshot CŨ thay vì tính lại.
        // BC15 tự đóng sổ lại (với số mới) khi bài này Done lần nữa.
        if (retry)
        {
            var reopened = await _db.Roadmaps
                .Where(r => r.Id == roadmapId && r.Status == RoadmapStatus.Completed)
                .ExecuteUpdateAsync(u => u
                    .SetProperty(r => r.Status, RoadmapStatus.Active)
                    .SetProperty(r => r.FinalReport, (string?)null)
                    .SetProperty(r => r.OverallComment, (string?)null)
                    .SetProperty(r => r.CompletedAt, (DateTime?)null), ct);
            if (reopened > 0)
                _logger.LogInformation(
                    "Roadmap {RoadmapId} mở lại (Completed -> Active) vì lesson {LessonId} được làm lại",
                    roadmapId, lessonId);
        }

        _logger.LogInformation(
            "BC14: {Action} lesson {LessonId} (roadmap {RoadmapId}) -> session {SessionId} Practicing (lần {AttemptNo})",
            retry ? "làm lại" : "/start", lessonId, roadmapId, sessionId, attemptNo);

        return response;
    }

    // Đọc lesson kèm milestone + roadmap (AsNoTracking). null → 404; roadmap khác chủ → 403.
    private async Task<RoadmapLesson> LoadOwnedLessonAsync(
        Guid candidateId, Guid roadmapId, Guid lessonId, CancellationToken ct)
    {
        var lesson = await _db.RoadmapLessons.AsNoTracking()
            .Include(l => l.Milestone).ThenInclude(m => m.Roadmap)
            .FirstOrDefaultAsync(l => l.Id == lessonId && l.Milestone.RoadmapId == roadmapId, ct)
            ?? throw new KeyNotFoundException("Không tìm thấy lesson này");

        if (lesson.Milestone.Roadmap.CandidateId != candidateId)
            throw new UnauthorizedAccessException("Không phải roadmap của bạn");

        return lesson;
    }

    /// <summary>
    /// F6a — điểm yếu THẬT của ứng viên ở đúng các tiêu chí mà bài học này nhắm tới.
    ///
    /// Trước đây luôn truyền `weaknesses: null`, nên nhánh `if weaknesses:` trong prompt AIService
    /// (prompts.py) là code CHẾT: đường ống đã thông từ interface tới prompt, chỉ thiếu mỗi dữ liệu.
    /// Hệ quả: bài học viết chung chung, không bám vào chỗ ứng viên đang yếu.
    ///
    /// Nguồn = `roadmap.Baseline` (tên tiêu chí → % lúc lập roadmap), vốn ĐÃ nằm sẵn trong entity đã
    /// `.Include()` ở LoadOwnedLessonAsync ⇒ 0 query thêm. Cố ý KHÔNG query `session_criterion_scores`
    /// cho tươi hơn: chính xác hơn chút nhưng tốn thêm 1 query trên đường lazy-gen vốn đã phải chờ AI
    /// đồng bộ, mà Baseline chính là snapshot của cùng dữ liệu đó.
    ///
    /// Giao với FocusCriteria để không "mách" AI những điểm yếu lạc đề với bài học đang mở.
    ///
    /// BE-5 — tách khỏi bước ĐỊNH DẠNG (<see cref="FormatWeaknesses"/>) để trả về DANH SÁCH TÊN
    /// THÔ, tái dùng cho <see cref="RoadmapEvidenceLoader"/> (cần tên tiêu chí, không cần chuỗi
    /// "Tên: 40%" đã ghép sẵn cho prompt).
    /// </summary>
    private List<RoadmapWeakness> FilterWeakCriteria(Roadmap roadmap, IReadOnlyList<string> focus)
    {
        if (roadmap.Baseline is not { Count: > 0 } baseline || focus.Count == 0)
            return [];

        return focus
            .Where(name => baseline.TryGetValue(name, out var pct)
                           && pct < _scoring.ImprovementThresholdPct)
            .Select(name => new RoadmapWeakness(name, baseline[name]))
            .ToList();
    }

    /// <summary>Định dạng cho prompt ("Tên tiêu chí: 40%"). Rỗng → null (giữ nguyên hành vi cũ,
    /// prompt bỏ qua nhánh weaknesses).</summary>
    private static List<string>? FormatWeaknesses(IReadOnlyList<RoadmapWeakness> weak)
        => weak.Count > 0 ? weak.Select(w => $"{w.CriterionName}: {w.Percentage:0.#}%").ToList() : null;

    private static LessonResponse MapLesson(RoadmapLesson l, int attemptCount)
        => new(l.Id, l.OrderNo, l.Title, l.TheoryContent, l.SessionId, l.Status.ToString(),
               (l.Resources ?? []).Select(MapResource).ToList(),
               // RAG grounding — nguồn AI đã cite (narrow ở OpenLessonAsync). null = chưa precompute.
               GroundingMapper.ToCitations(l.GroundingRefs),
               attemptCount,
               CanRetry(l.Status));

    /// <summary>
    /// Luật "được làm lại không" — MỘT chỗ duy nhất, dùng chung với <see cref="RoadmapService"/>.
    /// Hai bản sao của luật này lệch nhau nghĩa là FE hiện nút ở màn này mà không hiện ở màn kia,
    /// hoặc hiện nút rồi bấm vào ăn 409.
    ///
    /// Chỉ dựa vào trạng thái bài; quyền sở hữu là hiển nhiên (chỉ chủ roadmap đọc được response).
    /// KHÔNG dựa vào số dư ví — lý do ghi ở <see cref="LessonResponse.CanRetry"/>.
    /// </summary>
    internal static bool CanRetry(LessonStatus status) => status == LessonStatus.Done;

    /// <summary>F15 — entity → DTO. Dùng chung với <see cref="RoadmapService"/> để 2 đường trả
    /// cùng shape (chi tiết lesson vs roadmap detail).</summary>
    internal static LessonResourceResponse MapResource(LessonResource r)
        => new(r.Title, r.Type, r.Publisher, r.Url);
}
