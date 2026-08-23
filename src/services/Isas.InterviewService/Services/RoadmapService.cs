using System.Text;
using System.Text.Json;
using Isas.InterviewService.ApplicationDbContext;
using Isas.InterviewService.DTOs;
using Isas.InterviewService.Entities;
using Isas.InterviewService.Enums;
using Isas.InterviewService.Models;
using Isas.InterviewService.Services.Interfaces;
using Isas.Shared.Pagination;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Options;

namespace Isas.InterviewService.Services;

// BC12 (D20) — roadmap ôn tập cá nhân hoá B2C. Gom điểm yếu (session_criterion_scores, BC9) + CV
// → AIService /generate-roadmap (sync) → LƯU 3 bảng (AI KHÔNG ghi DB). Tạo roadmap KHÔNG trừ credit
// (D7/D15 — chỉ session luyện BC14 mới reserve). AI lỗi → 502, KHÔNG lưu gì (gọi AI trước khi Add).
// BC17 — candidate CHỌN nguồn: SessionIds (baseline), CvAnalysisId + PriorRoadmapId + Focus (bối cảnh
// prompt, KHÔNG vào baseline). Rỗng SessionIds → roadmap CHUẨN theo level (thôi tự gom mọi buổi Scored).
public class RoadmapService : IRoadmapService
{
    private readonly InterviewDbContext _db;
    private readonly IStorageService _storage;
    private readonly IAiServiceRoadmapGenerator _generator;
    private readonly IKnowledgeService? _knowledge;   // RAG grounding — precompute (null = tắt)
    private readonly GroundingOptions _grounding;     // RAG grounding — Enabled/TopK/threshold
    private readonly ILogger<RoadmapService> _logger;
    private readonly IEntitlementClient? _entitlements;
    private readonly RoadmapOptions _roadmap;         // ngưỡng buổi tối thiểu cho chế độ ôn tập
    private readonly bool _tieringEnabled;
    private readonly bool _bilingualEnabled;

    public RoadmapService(
        InterviewDbContext db,
        IStorageService storage,
        IAiServiceRoadmapGenerator generator,
        ILogger<RoadmapService> logger,
        // RAG grounding — optional (default null/tắt): test cũ dựng 4 tham số vẫn compile + precompute tắt.
        IKnowledgeService? knowledge = null,
        IOptions<GroundingOptions>? groundingOptions = null,
        IEntitlementClient? entitlements = null,
        IConfiguration? config = null,
        IOptions<RoadmapOptions>? roadmapOptions = null)
    {
        _db = db;
        _storage = storage;
        _generator = generator;
        _knowledge = knowledge;
        _grounding = groundingOptions?.Value ?? new GroundingOptions();
        _logger = logger;
        _entitlements = entitlements;
        _roadmap = roadmapOptions?.Value ?? new RoadmapOptions();
        _tieringEnabled = bool.TryParse(config?["Tiering:Enabled"], out var enabled) && enabled;
        _bilingualEnabled = bool.TryParse(config?["Interview:Bilingual:Enabled"], out var bilingual) && bilingual;
    }

    public async Task<RoadmapResponse> CreateAsync(
        Guid candidateId, CreateRoadmapRequest req, CancellationToken ct = default)
    {
        var language = ValidateLanguage(req.Language);
        // BE-6 — chuẩn hoá tên NGAY ĐẦU HÀM, trước cả kiểm gói và lời gọi AI: tên sai là lỗi đầu vào,
        // để nó nổ sau khi đã đốt một lượt Gemini là bắt người dùng trả giá cho lỗi gõ của mình.
        // Cùng lý do `ValidateLanguage` đứng ở đây — và cùng lý do `ValidateScope` (BE-4) đứng ngay dưới.
        var requestedName = RoadmapNaming.Normalize(req.Name);
        var createdAt = DateTime.UtcNow;
        var scope = ValidateScope(req.Scope);
        // Cùng lý do đã nêu cho `ValidateLanguage`/`ValidateScope`: lỗi đầu vào phải nổ TRƯỚC khi
        // đốt một lượt Gemini.
        var mode = ValidateMode(req.Mode);
        // Cùng lý do — trình độ hiện tại candidate tự khai ở wizard, kiểm TRƯỚC mọi I/O.
        var currentLevelOverride = ValidateCurrentLevel(req.CurrentLevel);
        if (_tieringEnabled && _entitlements is not null && !(await _entitlements.ResolveUserAsync(candidateId, ct)).RoadmapEnabled)
            throw new UnauthorizedAccessException("Gói hiện tại không bao gồm roadmap ôn tập.");
        // CV optional — VẪN kiểm chủ sở hữu (null → 404; khác chủ → 403; rỗng → 400) và vẫn lưu
        // `roadmaps.cv_id`, nhưng NỘI DUNG CV không còn đi vào prompt nữa.
        //
        // Vì sao gỡ: đo trên production, roadmap có CV và không CV cho tên chặng KHÔNG phân biệt
        // được, và nhóm có CV còn nêu công nghệ cụ thể ÍT hơn (8,6% vs 12,1% số bài). Prompt sinh
        // roadmap là bài toán dựng *cấu trúc giáo trình*, mà chủ đề của một nghề không đổi theo
        // người ⇒ CV thô không có chỗ tác động. Phần CV đóng góp được nay đi qua `cvAnalysisSummary`
        // (bản đã chưng cất, có sẵn Điểm mạnh/Điểm yếu/Gợi ý) và `currentLevel` (sàn trình độ).
        //
        // Giữ lại lời gọi kiểm quyền: bỏ nó đi thì người dùng gửi `cvId` của người khác sẽ nhận
        // 201 thay vì 403, tức nới quyền một cách âm thầm.
        if (req.CvId is not null)
            _ = await ReadOwnedParsedTextAsync(req.CvId.Value, candidateId, "CV", ct);

        // BC17 — baseline lấy từ CÁC BUỔI CANDIDATE CHỌN (thôi tự gom MỌI buổi Scored). SessionIds
        // rỗng/null → roadmap CHUẨN theo level (baseline/weakness/sources = null, KHÔNG query buổi nào).
        Dictionary<string, decimal>? baseline = null;
        List<RoadmapWeakness>? weaknesses = null;
        List<Guid>? sourceSessionIds = null;

        if (req.SessionIds is { Count: > 0 })
        {
            var requestedIds = req.SessionIds.Distinct().ToList();

            // CHỈ những buổi được chọn, owner-scoped + B2C + đã Scored (BC-3). Không phủ đủ MỌI id yêu
            // cầu → 404 batch (KHÔNG lộ id nào thiếu / không thuộc mình / chưa chấm).
            //
            // Vế "B2C + đã Scored" tách riêng thành .Where(RoadmapSessionEligibility.Predicate) —
            // MỘT nguồn sự thật dùng chung với PracticeService.GetHistoryAsync (wizard picker gọi
            // ?status=Scored&excludeCampaign=true). Gộp chung một Where() với id/owner thì vẫn dịch
            // đúng SQL (EF AND các vị từ), nhưng tách riêng biểu thức là thứ cho phép hai nơi CHIA
            // SẺ cùng một object thay vì chép tay hai lần.
            var chosen = await _db.PracticeSessions.AsNoTracking()
                .Include(s => s.CriterionScores)
                .Where(s => requestedIds.Contains(s.Id) && s.CandidateId == candidateId)
                .Where(RoadmapSessionEligibility.Predicate)
                .OrderByDescending(s => s.CreatedAt)
                .ToListAsync(ct);

            if (chosen.Count != requestedIds.Count)
                throw new KeyNotFoundException(
                    "Một số buổi luyện không tồn tại, không thuộc về bạn, hoặc chưa được chấm.");

            // 🔴 Buổi luyện phải CÙNG NGHỀ với lộ trình đang tạo. Guard sở hữu ở trên KHÔNG có vế
            // này, nên trước bản vá `POST /practice/roadmaps {jobCategory:"BE"}` kèm sessionIds của
            // buổi BA vẫn trả 201: baseline/điểm yếu/bằng chứng của nghề KHÁC chảy thẳng vào prompt,
            // trong khi `LoadCriteriaNamesAsync` bên dưới lại nạp tiêu chí của BE ⇒ AI được yêu cầu
            // bám một bộ tiêu chí không hề sinh ra con số nào nó đang đọc. Frontend đã lọc phía
            // client, nhưng UI giấu đi ≠ hợp đồng từ chối.
            //
            // 🔴 Vị trí BẮT BUỘC là ở ĐÂY — sau guard sở hữu, TRƯỚC guard `Reinforce` bên dưới:
            //   • sau guard sở hữu, vì thông báo này NÊU ĐÍCH DANH id, mà chỉ những id đã chứng minh
            //     thuộc người gọi mới được phép nêu tên. Guard sở hữu cố ý gộp "không tồn tại /
            //     không thuộc mình / chưa chấm" vào MỘT câu 404 câm để không lộ id nào của người
            //     khác tồn tại — nói về một id chưa qua cửa đó là mở lại đúng lỗ rò ấy.
            //   • trước guard `Reinforce`, vì buổi lệch nghề mà tình cờ không có tiêu chí nào cần
            //     cải thiện sẽ nhận câu "hãy chọn buổi khác" — đúng lời khuyên nhưng SAI nguyên
            //     nhân, người dùng đi chọn thêm buổi BA nữa và vẫn hỏng.
            var crossCategory = chosen.Where(s => s.JobCategory != req.JobCategory).ToList();
            if (crossCategory.Count > 0)
                throw CrossCategorySource(
                    "Buổi luyện đã chọn", req.JobCategory,
                    string.Join("; ", crossCategory
                        .GroupBy(s => s.JobCategory)
                        .OrderBy(g => g.Key)
                        .Select(g => $"{g.Key} ({string.Join(", ", g.Select(s => s.Id))})")));

            // Newest-first: tiêu chí xuất hiện lần đầu (buổi mới nhất) thắng → baseline = % hiện tại.
            var withScores = chosen.Where(s => s.CriterionScores.Count > 0).ToList();
            if (withScores.Count > 0)
            {
                baseline = new Dictionary<string, decimal>();
                var weak = new List<RoadmapWeakness>();
                foreach (var s in withScores)
                    foreach (var cs in s.CriterionScores)
                    {
                        if (baseline.ContainsKey(cs.CriterionName)) continue;
                        baseline[cs.CriterionName] = cs.Percentage;
                        if (cs.NeedsImprovement)
                            weak.Add(new RoadmapWeakness(cs.CriterionName, cs.Percentage));
                    }

                weaknesses = weak.Count > 0 ? weak : null;
            }

            // sourceSessionIds = ĐÚNG các buổi được chọn (đều đã Scored/owned nhờ guard phủ ở trên).
            sourceSessionIds = chosen.Select(s => s.Id).ToList();
        }

        // 🔴 Chế độ ôn tập BẮT BUỘC có dữ liệu điểm yếu — và khi thiếu thì phải NÓI RA, tuyệt đối
        // không âm thầm sinh một lộ trình LevelUp rồi dán nhãn "ôn tập". Nếu để rơi im lặng,
        // `build_roadmap_prompt` sẽ đi đúng nhánh else "ứng viên CHƯA có buổi luyện nào được chấm
        // → tạo roadmap CHUẨN theo level" — tức chính hành vi LevelUp — mà người dùng vẫn thấy
        // lộ trình của mình được ghi là Reinforce. Đây là lớp lỗi "nén im lặng" đã cắn dự án
        // nhiều lần (chọn Thực tập nhận Mới-tốt-nghiệp; chọn Lead nhận Senior).
        //
        // HAI guard TÁCH RỜI, mỗi cái một câu lỗi riêng, vì người dùng phải làm hai việc khác nhau:
        //   • thiếu BUỔI  → đi luyện thêm (hoặc chọn thêm buổi cũ vào danh sách),
        //   • thiếu ĐIỂM YẾU → các buổi đó không có tiêu chí nào cần cải thiện, phải chọn buổi khác.
        // Gộp thành một câu chung sẽ bảo người vừa luyện 5 buổi rất tốt rằng họ "chưa luyện đủ".
        if (mode == RoadmapMode.Reinforce)
        {
            var chosenCount = sourceSessionIds?.Count ?? 0;
            if (chosenCount < _roadmap.ReinforceMinSessions)
                throw new InvalidOperationException(
                    $"Chế độ ôn tập cần ít nhất {_roadmap.ReinforceMinSessions} buổi luyện đã được " +
                    $"chấm để biết bạn hay sai ở đâu (đang chọn {chosenCount}). Hãy luyện thêm rồi " +
                    "chọn các buổi đó, hoặc tạo lộ trình ở chế độ LevelUp.");

            if (weaknesses is not { Count: > 0 })
                throw new InvalidOperationException(
                    "Các buổi luyện đã chọn không có tiêu chí nào bị đánh dấu cần cải thiện, nên " +
                    "không có gì để ôn lại. Hãy chọn buổi khác, hoặc tạo lộ trình ở chế độ LevelUp.");
        }

        // BC17 — phân tích CV đã có (BC7) làm NGỮ CẢNH prompt. CHỈ ĐỌC row đã lưu — KHÔNG gọi lại
        // /analyze-cv, KHÔNG reserve/consume credit (D22, tạo roadmap free). Thiếu → 404; khác chủ → 403.
        string? cvAnalysisSummary = null;
        // Trình độ HIỆN TẠI suy từ CV — đi bằng KHOÁ RIÊNG xuống AIService, KHÔNG nhúng vào chuỗi
        // `cvAnalysisSummary`: chuỗi đó nằm trong khối prompt đã tuyên bố là DỮ LIỆU chứ không
        // phải lệnh, mà đây LÀ chỉ thị (bỏ phần nhập môn đã nắm) ⇒ nhét vào đó là tự vô hiệu hoá.
        string? currentLevel = null;
        if (req.CvAnalysisId is not null)
        {
            var ca = await _db.Set<CvAnalysis>().AsNoTracking()
                .FirstOrDefaultAsync(x => x.Id == req.CvAnalysisId.Value, ct)
                ?? throw new KeyNotFoundException("Phân tích CV không tồn tại.");
            if (ca.CandidateId != candidateId)
                throw new UnauthorizedAccessException("Không phải phân tích CV của bạn");
            // Cùng lý do đã nêu ở guard lệch nghề của buổi luyện, và cũng phải đứng SAU cửa sở hữu
            // vì câu lỗi nêu đích danh id. `cv_analyses.job_category` là nghề bản phân tích được
            // chạy cho — bản phân tích CV nghề BA nói về điểm mạnh/yếu của BA, và `CurrentLevel`
            // rút từ nó là trình độ BA; bơm cả hai vào lộ trình BE là đặt sàn trình độ sai nghề.
            if (ca.JobCategory != req.JobCategory)
                throw CrossCategorySource(
                    "Phân tích CV đã chọn", req.JobCategory, $"{ca.JobCategory} ({ca.Id})");
            cvAnalysisSummary = BuildCvAnalysisSummary(ca);
            currentLevel = ca.CurrentLevel;
        }
        // Giá trị candidate TỰ KHAI ở wizard THẮNG giá trị suy từ CV: người dùng biết trình độ
        // của mình rõ hơn một suy đoán, và một phần đáng kể bản phân tích CV không suy ra được gì
        // (xem Entities/CvAnalysis.cs) nên để CV thắng sẽ im lặng bỏ mất lựa chọn của người dùng.
        //
        // 🔴 Dòng này BẮT BUỘC nằm NGOÀI khối `if (req.CvAnalysisId is not null)` ở trên, chạy
        // VÔ ĐIỀU KIỆN. Nhét nó vào TRONG khối (`currentLevel = currentLevelOverride ?? ca.CurrentLevel;`
        // rồi xoá dòng này) sẽ làm candidate KHÔNG chọn bản phân tích CV nào — bỏ qua bước CV, một
        // nhánh hợp lệ đã chốt trong wizard — có `currentLevelOverride` bị RƠI IM LẶNG: họ chọn
        // trình độ ở bước 2, không lỗi gì, và lựa chọn đó biến mất trước khi tới prompt.
        currentLevel = currentLevelOverride ?? currentLevel;

        // BC17 — final_report của roadmap đã hoàn thành (BC15) làm NGỮ CẢNH. Thiếu → 404; khác chủ → 403;
        // chưa có báo cáo (chưa hoàn thành) → 400.
        string? priorRoadmapSummary = null;
        if (req.PriorRoadmapId is not null)
        {
            var prior = await _db.Set<Roadmap>().AsNoTracking()
                .FirstOrDefaultAsync(x => x.Id == req.PriorRoadmapId.Value, ct)
                ?? throw new KeyNotFoundException("Roadmap được chọn không tồn tại.");
            if (prior.CandidateId != candidateId)
                throw new UnauthorizedAccessException("Không phải roadmap của bạn");
            // Cùng lý do — và đứng TRƯỚC guard "chưa có báo cáo" bên dưới: một lộ trình BA đã hoàn
            // thành thì có báo cáo hợp lệ, nên nếu để guard kia chạy trước thì lộ trình BA sẽ lọt
            // qua sạch sẽ; còn lộ trình BA CHƯA hoàn thành lại nhận câu "chưa có báo cáo" — đúng
            // sự thật nhưng che mất nguyên nhân thật, người dùng đi hoàn thành nó rồi vẫn hỏng.
            if (prior.JobCategory != req.JobCategory)
                throw CrossCategorySource(
                    "Lộ trình tham chiếu đã chọn", req.JobCategory, $"{prior.JobCategory} ({prior.Id})");
            if (string.IsNullOrWhiteSpace(prior.FinalReport))
                throw new InvalidOperationException("Roadmap được chọn chưa có báo cáo (chưa hoàn thành).");

            priorRoadmapSummary = BuildPriorRoadmapSummary(prior.FinalReport, prior.Id);
        }

        // BC17 — mô tả tự do: trim (khoảng-trắng-thuần = không nhập) + cap độ dài → vượt 400. Chống
        // prompt-injection (bọc như dữ liệu) là việc phía AIService (worker Python), không phải ở đây.
        string? focus = null;
        if (!string.IsNullOrWhiteSpace(req.Focus))
        {
            focus = req.Focus.Trim();
            if (focus.Length > FocusMaxChars)
                throw new InvalidOperationException($"Mô tả focus vượt quá {FocusMaxChars} ký tự.");
        }

        // BE-1 — tiêu chí năng lực THẬT của (nghề, ngôn ngữ) này, để milestone.focusCriteria chọn
        // NGUYÊN VĂN thay vì bịa tên (đo trên production: chỉ 7% focusCriteria khớp tên tiêu chí
        // thật khi AIService không được cấp danh sách này).
        var criteria = await LoadCriteriaNamesAsync(candidateId, req.JobCategory, language, ct);

        // BE-5 — bằng chứng hành vi: Reasoning (E11) của answer điểm THẤP NHẤT cho ≤3 tiêu chí yếu
        // nhất, thay cho việc chỉ gửi con số %. `sourceSessionIds`/`weaknesses` null → rỗng (không
        // chọn buổi nào ⇒ không có gì để trích).
        var evidence = weaknesses is { Count: > 0 } && sourceSessionIds is { Count: > 0 }
            ? await RoadmapEvidenceLoader.LoadAsync(_db, sourceSessionIds, weaknesses, ct)
            : [];

        // Gọi AIService sinh cấu trúc (sync). Lỗi → AiServiceException (502) → KHÔNG lưu gì.
        var ai = language == "vi"
            ? await _generator.GenerateAsync(req.JobCategory.ToString(), req.Level.ToString(), weaknesses,
                focus, cvAnalysisSummary, priorRoadmapSummary, criteria, scope, evidence, mode, currentLevel, ct)
            : await _generator.GenerateAsync(req.JobCategory.ToString(), req.Level.ToString(), weaknesses,
                focus, cvAnalysisSummary, priorRoadmapSummary, ct, language, criteria, scope, evidence, mode, currentLevel);

        var roadmap = new Roadmap
        {
            Id = Guid.NewGuid(),
            CandidateId = candidateId,
            // BE-6 — tên người dùng gửi đã được chuẩn hoá ở ĐẦU hàm (trước lời gọi AI); vắng thì
            // sinh mặc định tại đây để `CreatedAt` dùng cho tên khớp đúng giá trị vừa gán bên dưới.
            Name = requestedName ?? RoadmapNaming.BuildDefault(req.JobCategory, req.Level, language, createdAt),
            JobCategory = req.JobCategory,
            Mode = mode,
            Level = req.Level,
            Language = language,
            CvId = req.CvId,
            SourceSessionIds = sourceSessionIds,
            Baseline = baseline,
            Status = RoadmapStatus.Active,
            CreatedAt = createdAt
        };

        var milestoneOrder = 1;
        foreach (var m in ai.Milestones)
        {
            var milestone = new RoadmapMilestone
            {
                Id = Guid.NewGuid(),
                OrderNo = milestoneOrder++,
                Title = m.Title,
                FocusCriteria = m.FocusCriteria.ToList(),
                Status = MilestoneStatus.Pending
            };

            var lessonOrder = 1;
            foreach (var l in m.Lessons)
                milestone.Lessons.Add(new RoadmapLesson
                {
                    Id = Guid.NewGuid(),
                    OrderNo = lessonOrder++,
                    Title = l.Title,
                    Status = LessonStatus.Theory,
                    TheoryContent = null
                });

            roadmap.Milestones.Add(milestone);
        }

        // RAG grounding (Cách 2 — precompute): batch-embed query từng lesson (tên bài + focus milestone +
        // jobCategory) trong 1 lần /embed → Qdrant search → LƯU snapshot vào lesson.GroundingRefs. Lúc MỞ
        // lesson (OpenLessonAsync) feed thẳng snapshot này, KHÔNG retrieve realtime (không thêm độ trễ lazy).
        // best-effort — RetrieveBatchAsync tự degrade rỗng khi lỗi; wrap để có sự cố lạ vẫn KHÔNG chặn tạo roadmap.
        if (_grounding.Enabled && _knowledge is not null)
            await PrecomputeLessonGroundingAsync(roadmap, req.JobCategory.ToString(), ct);

        _db.Set<Roadmap>().Add(roadmap);
        await _db.SaveChangesAsync(ct);

        _logger.LogInformation(
            "BC12: roadmap {Id} candidate {CandidateId} ({Cat}/{Level}/{Mode}) milestones={M} sources={S}",
            roadmap.Id, candidateId, req.JobCategory, req.Level, mode,
            roadmap.Milestones.Count, sourceSessionIds?.Count ?? 0);

        // Roadmap vừa tạo — chưa bài nào được làm, nên rỗng là ĐÚNG do cấu trúc (không phải bỏ sót).
        return Map(roadmap, includeTheory: true, attemptCounts: EmptyAttemptCounts, scope: scope);
    }

    // RAG grounding (Cách 2) — precompute snapshot cho MỌI lesson trong roadmap. 1 lần /embed cho tất cả
    // query → search từng lesson → set GroundingRefs (LIST rỗng nếu miss — grounding ĐÃ chạy nên KHÔNG null,
    // phân biệt với roadmap cũ chưa precompute = null). Query = tên bài + focus milestone + jobCategory.
    private async Task PrecomputeLessonGroundingAsync(Roadmap roadmap, string jobCategory, CancellationToken ct)
    {
        // Duyệt lesson theo đúng thứ tự để map kết quả batch về đúng lesson.
        var flat = roadmap.Milestones
            .SelectMany(m => m.Lessons.Select(l => (Lesson: l, Milestone: m)))
            .ToList();
        if (flat.Count == 0) return;

        var queries = flat.Select(x =>
        {
            var parts = new List<string> { x.Lesson.Title, jobCategory };
            if (x.Milestone.FocusCriteria is { Count: > 0 } focus)
                parts.Insert(1, string.Join(", ", focus));
            return string.Join("\n", parts);
        }).ToList();

        IReadOnlyList<IReadOnlyList<GroundingChunk>> batches;
        try
        {
            batches = await _knowledge!.RetrieveBatchAsync(jobCategory, queries, ct);
        }
        catch (Exception ex)
        {
            // RetrieveBatchAsync vốn tự degrade; wrap phòng lỗi lạ → precompute rỗng, KHÔNG chặn tạo roadmap.
            _logger.LogWarning(ex, "RAG grounding: precompute roadmap lỗi → grounding_refs=[] (ungrounded)");
            foreach (var (lesson, _) in flat) lesson.GroundingRefs = new List<GroundingChunk>();
            return;
        }

        for (int i = 0; i < flat.Count; i++)
            // grounding ĐÃ chạy → LUÔN set list (rỗng = ungrounded), KHÔNG để null.
            flat[i].Lesson.GroundingRefs = (batches.Count > i ? batches[i] : Array.Empty<GroundingChunk>()).ToList();

        var grounded = flat.Count(x => x.Lesson.GroundingRefs is { Count: > 0 });
        _logger.LogInformation(
            "RAG grounding: precompute roadmap {Id} — {Grounded}/{Total} lesson có nguồn",
            roadmap.Id, grounded, flat.Count);
    }

    /// <summary>
    /// BE-1 — tên THẬT của mọi tiêu chí năng lực (nghề, ngôn ngữ) đang hiệu lực cho candidate, để
    /// AIService chỉ chọn <c>milestone.focusCriteria</c> bằng cách sao chép NGUYÊN VĂN từ đây thay
    /// vì tự bịa tên.
    ///
    /// Dùng CHUNG <see cref="B2CRubricScope"/> với mọi chỗ chọn tiêu chí B2C khác (publish · callback
    /// · republisher · BC9 · <c>LoadTargetableCriteriaAsync</c> của <c>PracticeService</c>): resolve
    /// khác đi ở đây thì tên gửi cho roadmap trỏ vào một bộ rubric KHÁC bộ dùng để chấm điểm thật.
    ///
    /// KHÔNG lọc theo <see cref="ScoringScope"/> (khác <c>LoadTargetableCriteriaAsync</c> chỉ lấy
    /// <c>WhenTargeted</c>) — roadmap cần TOÀN BỘ tên tiêu chí năng lực (cả 4 tiêu chí CÁCH NÓI lẫn
    /// 3 tiêu chí NỘI DUNG của seed B2C), vì milestone có thể hợp lý nhắm cả hai nhóm.
    /// </summary>
    private async Task<List<QuestionTargetCriterionDto>> LoadCriteriaNamesAsync(
        Guid candidateId, JobCategory jobCategory, string language, CancellationToken ct)
    {
        var owner = await B2CRubricScope.ResolveOwnerAsync(_db, candidateId, jobCategory, language, ct);
        var query = _db.RubricCriteria.AsNoTracking()
            .Where(c => c.IsActive && c.CampaignId == null
                        && c.JobCategory == jobCategory && c.Language == language);
        query = owner is Guid oid
            ? query.Where(c => c.CandidateId == oid)
            : query.Where(c => c.CandidateId == null);

        return await query
            .OrderBy(c => c.Name)
            .Select(c => new QuestionTargetCriterionDto(c.Id, c.Name))
            .ToListAsync(ct);
    }

    /// <summary>
    /// BE-6 — đổi tên lộ trình. Trả `null` khi không tồn tại (404); khác chủ → 403.
    ///
    /// Cho đổi ở MỌI trạng thái, kể cả `Completed`: tên là nhãn người dùng tự đặt để phân biệt các
    /// lộ trình của mình, không phải dữ liệu kết quả bị đóng băng khi học xong.
    /// </summary>
    public async Task<RoadmapResponse?> RenameAsync(
        Guid candidateId, Guid id, string? requestedName, CancellationToken ct = default)
    {
        // Ở đường ĐỔI TÊN, tên là thứ DUY NHẤT người dùng gửi — nên `null` cũng là đầu vào sai,
        // khác đường TẠO nơi `null` hợp lệ và có nghĩa "để server đặt hộ".
        var name = RoadmapNaming.Normalize(requestedName)
            ?? throw new InvalidOperationException("Tên lộ trình không được để trống.");

        var r = await _db.Set<Roadmap>()
            .Include(x => x.Milestones).ThenInclude(m => m.Lessons)
            .FirstOrDefaultAsync(x => x.Id == id, ct);

        if (r is null) return null;                                        // 404
        if (r.CandidateId != candidateId)
            throw new UnauthorizedAccessException("Không phải roadmap của bạn");   // 403

        r.Name = name;
        await _db.SaveChangesAsync(ct);

        return Map(r, includeTheory: false, await LoadAttemptCountsAsync(id, ct));
    }

    public async Task<RoadmapResponse?> GetAsync(
        Guid candidateId, Guid id, CancellationToken ct = default)
    {
        var r = await _db.Set<Roadmap>().AsNoTracking()
            .Include(x => x.Milestones).ThenInclude(m => m.Lessons)
            .FirstOrDefaultAsync(x => x.Id == id, ct);

        if (r is null) return null;                                        // 404
        if (r.CandidateId != candidateId)
            throw new UnauthorizedAccessException("Không phải roadmap của bạn");   // 403

        return Map(r, includeTheory: true, await LoadAttemptCountsAsync(id, ct));
    }

    /// <summary>
    /// Danh sách roadmap của chính user — keyset-paged (mẫu DB8/DB31), KHÔNG kèm cây milestone/lesson.
    ///
    /// Trước: <c>Include(Milestones).ThenInclude(Lessons)</c> + không phân trang ⇒ payload nhân theo
    /// cây (roadmap × milestone × lesson) và không có trần, cho một màn hình danh sách chỉ vẽ tiêu đề
    /// + ngày + trạng thái. Nay project thẳng <see cref="RoadmapSummaryResponse"/> trong SQL; ai cần
    /// cây đầy đủ thì gọi <c>GET /roadmaps/{id}</c> (giữ nguyên <see cref="RoadmapResponse"/>).
    /// Đã đối chiếu FE trước khi bỏ: trang danh sách không đọc <c>milestones</c> (chỉ trang chi tiết
    /// đọc, và nó gọi endpoint khác).
    /// </summary>
    public async Task<KeysetPage<RoadmapSummaryResponse>> ListAsync(
        Guid candidateId, string? cursor = null, int? limit = null,
        string? status = null, bool? hasFinalReport = null, CancellationToken ct = default)
    {
        var take = KeysetPaging.ClampLimit(limit);
        var cur = KeysetCursor.Decode(cursor);

        var query = _db.Set<Roadmap>().AsNoTracking()
            .Where(x => x.CandidateId == candidateId);

        // ── Lọc OPT-IN cho picker "chọn lộ trình đã hoàn tất" của wizard ────────────────────
        //
        // 🔑 Lọc PHẢI nằm ở đây, TRƯỚC `Take` — tức trong SQL, không phải sau khi đã lấy trang.
        // Trước bản này client lọc trên TRANG ĐẦU của keyset paging: người có nhiều lộ trình thì
        // cái đã hoàn tất nằm ở trang 2 trở đi BIẾN MẤT khỏi dropdown mà không ai biết — không
        // báo lỗi, không dòng trống, chỉ là một lựa chọn đáng lẽ có mà không thấy.
        var parsedStatus = ValidateRoadmapStatus(status);
        if (parsedStatus is not null)
            query = query.Where(x => x.Status == parsedStatus.Value);

        // ⚠ `hasFinalReport` mới là vị ngữ ĐÚNG NGHIỆP VỤ, không phải `status == Completed`:
        // `CreateAsync` gác bằng `IsNullOrWhiteSpace(prior.FinalReport)` → 400, còn
        // `RoadmapLessonService.RetryLessonAsync` mở lại roadmap `Completed → Active` và XOÁ
        // `FinalReport` ⇒ hai vị ngữ KHÔNG đồng nhất theo cả hai chiều. Lọc theo status là mời một
        // lộ trình rồi để người dùng ăn 400 SAU KHI đã chờ 13–54s tạo roadmap. Giữ cả hai tham số
        // vì chúng trả lời hai câu hỏi khác nhau; picker của wizard dùng `hasFinalReport`.
        //
        // Vị ngữ so `!= null` khớp CHÍNH XÁC cách cột được chiếu ra `HasFinalReport` ở dưới —
        // lệch nhau thì cờ trả về và tập được lọc mâu thuẫn nhau. Lý do không thêm `&& != ""`:
        // xem chú thích tại chỗ chiếu.
        if (hasFinalReport == true)
            query = query.Where(x => x.FinalReport != null);
        else if (hasFinalReport == false)
            query = query.Where(x => x.FinalReport == null);

        // Keyset (CreatedAt DESC, Id DESC) — Id tie-break để hai roadmap trùng created_at vẫn có thứ
        // tự tổng, không lặp/sót dòng khi lật trang.
        if (cur is not null)
            query = query.Where(x => x.CreatedAt < cur.CreatedAt
                || (x.CreatedAt == cur.CreatedAt && x.Id.CompareTo(cur.Id) < 0));

        // BE-6 — chiếu các cột CẦN xuống SQL rồi mới dựng response trong bộ nhớ. Không gọi
        // `RoadmapNaming.Resolve` thẳng trong `.Select` được: EF phải dịch cây biểu thức sang SQL và
        // sẽ hoặc ném, hoặc âm thầm kéo cả bảng về client để đánh giá. Vẫn KHÔNG có `Include` cây
        // milestone→lesson — xem chú thích trên `RoadmapSummaryResponse` về lý do list bỏ nó.
        var raw = await query
            .OrderByDescending(x => x.CreatedAt)
            .ThenByDescending(x => x.Id)
            .Take(take)
            .Select(x => new
            {
                x.Id,
                x.Name,
                x.JobCategory,
                x.Level,
                x.Mode,
                x.Language,
                x.CvId,
                x.Status,
                x.CreatedAt,
                x.CompletedAt,
                // Tính TRONG SQL, KHÔNG kéo cả `final_report` về: cột đó là jsonb chứa nguyên báo
                // cáo tổng kết, kéo về chỉ để kiểm rỗng là phình payload của một endpoint DANH SÁCH.
                //
                // ⚠ CỐ Ý chỉ so `!= null`, KHÔNG thêm `&& != ""`: cột là jsonb, so với chuỗi rỗng
                // trong SQL là chỗ Npgsql và SQLite hành xử khác nhau (SQLite lưu jsonb như TEXT nên
                // test vẫn xanh trong khi Postgres vỡ) — đúng lớp bug đã cắn repo nhiều lần. Không
                // cần vế đó: chỉ có HAI chỗ ghi cột này (RoadmapReportService đặt JSON khi hoàn tất,
                // RoadmapLessonService.RetryLessonAsync đặt `null` khi làm lại), không đường nào sinh
                // ra chuỗi rỗng — đo trên dev: 3/3 Completed non-null, 26/26 Active null, 0 rỗng.
                HasFinalReport = x.FinalReport != null
            })
            .ToListAsync(ct);

        var rows = raw
            .Select(x => new RoadmapSummaryResponse(
                x.Id,
                RoadmapNaming.Resolve(x.Name, x.JobCategory, x.Level, x.Language, x.CreatedAt),
                x.JobCategory.ToString(),
                x.Level.ToString(),
                x.Mode.ToString(),
                x.CvId,
                x.Status.ToString(),
                x.CreatedAt,
                x.CompletedAt,
                x.HasFinalReport))
            .ToList();

        var next = rows.Count == take
            ? new KeysetCursor(rows[^1].CreatedAt, rows[^1].Id).Encode()
            : null;
        return new KeysetPage<RoadmapSummaryResponse>(rows, next);
    }

    // BC17 — trần độ dài. focus: cap input tự do (rẻ, chống prompt phình). summary: cắt bối cảnh gửi AI
    // (giữ HttpClient timeout + chi phí token trong tầm — nhồi nhiều report/CV dễ vượt).
    private const int FocusMaxChars = 2000;
    private const int SummaryMaxChars = 4000;

    // BC17 — deserialize final_report khớp cách RoadmapReportService serialize (Web defaults).
    private static readonly JsonSerializerOptions WebJson = new(JsonSerializerDefaults.Web);

    // BC17 — dựng bối cảnh text từ 1 phân tích CV (BC7) đã lưu: summary + strengths/weaknesses/suggestions
    // + mức khớp JD (nếu có). Cắt ≤ SummaryMaxChars.
    private static string BuildCvAnalysisSummary(CvAnalysis ca)
    {
        var sb = new StringBuilder();
        if (!string.IsNullOrWhiteSpace(ca.Summary))
            sb.Append("Tóm tắt CV: ").AppendLine(ca.Summary.Trim());
        if (ca.Strengths.Count > 0)
            sb.Append("Điểm mạnh: ").AppendLine(string.Join("; ", ca.Strengths));
        if (ca.Weaknesses.Count > 0)
            sb.Append("Điểm yếu: ").AppendLine(string.Join("; ", ca.Weaknesses));
        if (ca.Suggestions.Count > 0)
            sb.Append("Gợi ý: ").AppendLine(string.Join("; ", ca.Suggestions));
        if (ca.JdMatch is not null)
            sb.Append("Mức khớp JD: ").Append(ca.JdMatch.Score).AppendLine("%");
        return Truncate(sb.ToString().Trim(), SummaryMaxChars);
    }

    // BC17 — dựng bối cảnh text từ final_report roadmap trước (BC15): overallComment + strengths/weaknesses
    // /improvements. Cắt ≤ SummaryMaxChars. final_report hỏng (defensive, đáng lẽ không xảy ra) → null.
    private string? BuildPriorRoadmapSummary(string finalReportJson, Guid roadmapId)
    {
        RoadmapReportResponse? report;
        try
        {
            report = JsonSerializer.Deserialize<RoadmapReportResponse>(finalReportJson, WebJson);
        }
        catch (JsonException ex)
        {
            _logger.LogWarning(ex, "BC17: final_report roadmap {RoadmapId} hỏng → bỏ qua bối cảnh", roadmapId);
            return null;
        }
        if (report is null) return null;

        var sb = new StringBuilder();
        if (!string.IsNullOrWhiteSpace(report.OverallComment))
            sb.Append("Nhận xét roadmap trước: ").AppendLine(report.OverallComment!.Trim());
        if (report.Strengths.Count > 0)
            sb.Append("Điểm mạnh: ").AppendLine(string.Join("; ", report.Strengths));
        if (report.Weaknesses.Count > 0)
            sb.Append("Điểm yếu: ").AppendLine(string.Join("; ", report.Weaknesses));
        if (report.Improvements.Count > 0)
            sb.Append("Đã cải thiện / cần luyện tiếp: ").AppendLine(string.Join("; ", report.Improvements));

        var text = sb.ToString().Trim();
        return text.Length == 0 ? null : Truncate(text, SummaryMaxChars);
    }

    private static string Truncate(string s, int max) => s.Length <= max ? s : s[..max];

    // Đọc parsed_text của file thuộc về candidate. null → 404; khác chủ → 403; rỗng → 400 (mẫu CvAnalysisService).
    private async Task<string> ReadOwnedParsedTextAsync(
        Guid fileId, Guid candidateId, string label, CancellationToken ct)
    {
        var file = await _storage.GetMetadata(fileId, ct)
            ?? throw new KeyNotFoundException($"{label} không tồn tại");

        if (file.UserId != candidateId)
            throw new UnauthorizedAccessException($"Không phải file {label} của bạn");

        if (string.IsNullOrWhiteSpace(file.ParsedText))
            throw new InvalidOperationException($"{label} không đọc được nội dung");

        return file.ParsedText;
    }

    // BK36 — chỉ `null` (client KHÔNG gửi field) mới rơi về mặc định "vi". Chuỗi rỗng là GIÁ TRỊ SAI,
    // phải bị từ chối chứ không được âm thầm nuốt thành "vi" — mẫu khớp PracticeService.ValidateLanguage.
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

    /// <summary>
    /// Câu 400 dùng chung cho MỌI nguồn dữ liệu lệch nghề (buổi luyện · phân tích CV · lộ trình
    /// tham chiếu). Ba chỗ gọi chứ không phải một, vì người dùng bỏ chọn ba nguồn đó ở ba chỗ khác
    /// nhau trên wizard và câu lỗi phải chỉ đúng chỗ cần sửa (mẫu hai guard tách rời của chế độ
    /// <c>Reinforce</c>) — nhưng cách DIỄN ĐẠT chỉ được định nghĩa MỘT LẦN ở đây, vì đây là cùng
    /// một khái niệm và ba câu chữ trôi khỏi nhau sẽ khiến frontend phải nhận dạng ba dạng lỗi.
    ///
    /// <para><c>offending</c> LUÔN nêu nghề thật + id, nên chỉ được truyền vào những id ĐÃ qua cửa
    /// kiểm sở hữu — xem ghi chú tại từng chỗ gọi.</para>
    /// </summary>
    private static InvalidOperationException CrossCategorySource(
        string source, JobCategory wanted, string offending)
        => new($"{source} thuộc nghề khác với lộ trình đang tạo ({wanted}): {offending}. " +
               "Hãy bỏ chọn nguồn lệch nghề, hoặc đổi nghề của lộ trình cho khớp.");

    // BE-4 — độ dài roadmap candidate CHỌN. Tập đóng, case-sensitive (mẫu ValidateSeniority của
    // PracticeService) — chỉ `null` (client KHÔNG gửi field) mặc định "Standard"; chuỗi rỗng/giá
    // trị lạ là GIÁ TRỊ SAI, bị từ chối 400 chứ không âm thầm rơi về mặc định (BK36).
    private static readonly string[] AllowedScopes = ["Quick", "Standard"];
    private const string DefaultScope = "Standard";

    private static string ValidateScope(string? requested)
    {
        if (requested is null) return DefaultScope;
        var scope = requested.Trim();
        if (!AllowedScopes.Contains(scope, StringComparer.Ordinal))
            throw new InvalidOperationException(
                $"scope chỉ nhận {string.Join(" / ", AllowedScopes)} (đang gửi: '{requested}').");
        return scope;
    }

    /// <summary>
    /// Chế độ lộ trình candidate CHỌN. Tập đóng, case-sensitive — mẫu <see cref="ValidateScope"/>:
    /// chỉ <c>null</c> (client KHÔNG gửi field) mới mặc định <c>LevelUp</c>; chuỗi rỗng hoặc giá
    /// trị lạ là GIÁ TRỊ SAI và bị từ chối 400, KHÔNG âm thầm rơi về mặc định (BK36).
    ///
    /// ⚠ Nghiêm hơn phía AIService có chủ đích: <c>app.roadmap_mode.normalize_mode</c> fail-OPEN
    /// (giá trị lạ → LevelUp) vì ở đó một lỗi gõ chỉ nên làm mất tính năng chứ không nên thành
    /// 502. Chỗ TỪ CHỐI phải là đây — nơi biết đây là request của người dùng thật và trả lời
    /// được cho họ biết sai chỗ nào.
    /// </summary>
    private static RoadmapMode ValidateMode(string? requested)
    {
        if (requested is null) return RoadmapMode.LevelUp;
        // Enum.TryParse mặc định CHẤP NHẬN cả chuỗi số ("1" → Reinforce) lẫn sai hoa/thường —
        // cả hai đều là đầu vào ta không hứa hỗ trợ, nên so khớp tường minh thay vì dùng nó.
        var mode = requested.Trim();
        if (mode == nameof(RoadmapMode.LevelUp)) return RoadmapMode.LevelUp;
        if (mode == nameof(RoadmapMode.Reinforce)) return RoadmapMode.Reinforce;
        throw new InvalidOperationException(
            $"mode chỉ nhận {nameof(RoadmapMode.LevelUp)} / {nameof(RoadmapMode.Reinforce)} " +
            $"(đang gửi: '{requested}').");
    }

    /// <summary>
    /// Trạng thái lọc cho <c>GET /roadmaps</c>. Tập đóng, case-sensitive — cùng khuôn
    /// <see cref="ValidateMode"/> / <c>PracticeService.ValidateHistoryStatus</c>: <c>null</c>/rỗng =
    /// không lọc; giá trị lạ = GIÁ TRỊ SAI và bị từ chối 400, KHÔNG âm thầm bỏ qua filter.
    ///
    /// <para>Vì sao khớp chặt chứ không <c>Enum.TryParse</c>: nó nhận cả chuỗi số (<c>"1"</c> →
    /// <c>Completed</c>) lẫn sai hoa/thường — hai thứ ta không hứa hỗ trợ. Và vì sao không fail-open:
    /// endpoint này nuôi PICKER của wizard, nơi "không lọc được" KHÔNG vô hại — nó trả lại lộ trình
    /// chưa có báo cáo cho người dùng chọn, rồi <c>CreateAsync</c> từ chối bằng 400 sau khi họ đã chờ
    /// tạo roadmap. Cùng lập luận đã dùng cho <c>?status=</c> của <c>/practice/history</c>.</para>
    /// </summary>
    private static RoadmapStatus? ValidateRoadmapStatus(string? requested)
    {
        if (string.IsNullOrWhiteSpace(requested)) return null;
        var value = requested.Trim();
        foreach (var name in Enum.GetNames<RoadmapStatus>())
            if (string.Equals(value, name, StringComparison.Ordinal))
                return Enum.Parse<RoadmapStatus>(name);
        throw new InvalidOperationException(
            $"status chỉ nhận {string.Join(" / ", Enum.GetNames<RoadmapStatus>())} " +
            $"(đang gửi: '{requested}').");
    }

    /// <summary>
    /// Trình độ NGHỀ NGHIỆP HIỆN TẠI candidate tự khai ở wizard. Tập đóng, case-sensitive — mẫu
    /// <see cref="ValidateMode"/>: chỉ <c>null</c> (client KHÔNG gửi field) mới giữ hành vi cũ
    /// (suy từ <c>cv_analyses</c>, xem <c>CreateAsync</c>); chuỗi rỗng hoặc giá trị lạ là GIÁ TRỊ
    /// SAI và bị từ chối 400, KHÔNG âm thầm rơi về mặc định (BK36).
    /// </summary>
    private static string? ValidateCurrentLevel(string? requested)
    {
        if (requested is null) return null;
        // Enum.TryParse mặc định CHẤP NHẬN cả chuỗi số ("1" → Junior) lẫn sai hoa/thường — cả hai
        // đều là đầu vào ta không hứa hỗ trợ, nên so khớp tường minh thay vì dùng nó (mẫu ValidateMode).
        var level = requested.Trim();
        if (level == nameof(RoadmapLevel.Fresher)) return level;
        if (level == nameof(RoadmapLevel.Junior)) return level;
        if (level == nameof(RoadmapLevel.Middle)) return level;
        if (level == nameof(RoadmapLevel.Senior)) return level;
        throw new InvalidOperationException(
            $"currentLevel chỉ nhận {nameof(RoadmapLevel.Fresher)} / {nameof(RoadmapLevel.Junior)} / " +
            $"{nameof(RoadmapLevel.Middle)} / {nameof(RoadmapLevel.Senior)} (đang gửi: '{requested}').");
    }

    private static readonly IReadOnlyDictionary<Guid, int> EmptyAttemptCounts =
        new Dictionary<Guid, int>();

    /// <summary>
    /// Số lần đã làm của MỌI bài trong 1 lộ trình — 1 truy vấn gộp, không N+1.
    /// Bài chưa từng làm không có dòng nào ⇒ vắng khỏi dictionary ⇒ <c>Map</c> đọc ra 0.
    /// </summary>
    private async Task<IReadOnlyDictionary<Guid, int>> LoadAttemptCountsAsync(
        Guid roadmapId, CancellationToken ct)
        => await _db.Set<RoadmapLessonAttempt>().AsNoTracking()
            .Where(a => a.Lesson.Milestone.RoadmapId == roadmapId)
            .GroupBy(a => a.LessonId)
            .Select(g => new { LessonId = g.Key, Count = g.Count() })
            .ToDictionaryAsync(x => x.LessonId, x => x.Count, ct);

    /// <summary>
    /// <paramref name="attemptCounts"/> = số lần đã làm, theo lessonId. THAM SỐ BẮT BUỘC (không có
    /// giá trị mặc định) là có chủ đích: một call site mới quên nạp sẽ phải TỰ QUYẾT truyền gì, thay
    /// vì âm thầm nhận 0 và nói dối FE là bài chưa từng được làm.
    /// </summary>
    private static RoadmapResponse Map(
        Roadmap r, bool includeTheory, IReadOnlyDictionary<Guid, int> attemptCounts,
        string? scope = null) => new(
        r.Id,
        RoadmapNaming.Resolve(r.Name, r.JobCategory, r.Level, r.Language, r.CreatedAt),
        r.JobCategory.ToString(),
        r.Level.ToString(),
        // Đọc THẲNG con dấu của lộ trình, không suy lại từ trạng thái hôm nay (mẫu `rubricSource`
        // của BC-8): người dùng phải thấy đúng chế độ mình đã chọn lúc tạo.
        r.Mode.ToString(),
        r.Language,
        r.CvId,
        r.Status.ToString(),
        r.Milestones.OrderBy(m => m.OrderNo).Select(m => new MilestoneResponse(
            m.Id,
            m.OrderNo,
            m.Title,
            m.FocusCriteria,
            m.Status.ToString(),
            m.Improvement is null
                ? null
                : m.Improvement.Select(kv => new MilestoneImprovementResponse(kv.Key, kv.Value)).ToList(),
            m.Lessons.OrderBy(l => l.OrderNo).Select(l => new LessonResponse(
                l.Id,
                l.OrderNo,
                l.Title,
                includeTheory ? l.TheoryContent : null,
                l.SessionId,
                l.Status.ToString(),
                // F15 — tài liệu đi CÙNG lý thuyết (sinh chung 1 lượt): view nào giấu theory thì
                // cũng giấu resources, tránh hiện "tài liệu" cho lesson chưa mở.
                includeTheory
                    ? l.Resources.Select(RoadmapLessonService.MapResource).ToList()
                    : [],
                // RAG grounding — CHỈ hiện citation khi lý thuyết ĐÃ sinh (grounding_refs lúc đó = tập AI thật
                // sự cite, narrow ở OpenLessonAsync). Chưa mở → null (chưa claim nguồn nào).
                includeTheory && l.TheoryContent != null
                    ? GroundingMapper.ToCitations(l.GroundingRefs)
                    : null,
                attemptCounts.TryGetValue(l.Id, out var attempts) ? attempts : 0,
                // Luật "được làm lại không" dùng CHUNG một hàm với đường chi tiết lesson — hai bản
                // sao lệch nhau nghĩa là FE hiện nút ở màn này mà không hiện ở màn kia.
                RoadmapLessonService.CanRetry(l.Status))).ToList()
        )).ToList(),
        r.CreatedAt,
        r.CompletedAt,
        // BE-4 — provenance: `sourceSessionIds`/`baseline` được GHI xuống DB từ BC12 nhưng trước đây
        // KHÔNG endpoint nào đọc lại — candidate chọn report trong wizard rồi sau khi tạo xong KHÔNG
        // CÒN cách nào xem lại đã dựa trên gì. `Scope` KHÔNG được lưu (không migration cho task này)
        // nên CHỈ có nghĩa NGAY LÚC TẠO (CreateAsync truyền `scope` từ request đang xử lý); đọc lại
        // roadmap CŨ (GetAsync) không biết scope lúc tạo → `null`, KHÔNG suy đoán từ số milestone/
        // lesson hiện có (mẫu BK23: null = không biết, đừng bịa "khác" từ "không biết").
        new RoadmapResolvedFromResponse(
            r.SourceSessionIds ?? [],
            r.Baseline is not null,
            scope));
}
