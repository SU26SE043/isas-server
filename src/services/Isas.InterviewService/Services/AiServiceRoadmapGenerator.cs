using System.Net.Http.Json;
using System.Text.Json;
using Isas.InterviewService.DTOs;
using Isas.InterviewService.Entities;
using Isas.InterviewService.Enums;
using Isas.InterviewService.Services.Interfaces;

namespace Isas.InterviewService.Services;

// BC12 — typed HttpClient gọi AIService `/generate-roadmap` (sync). Mẫu AiServiceCvAnalyzer.
// Request {jobCategory, level, weaknesses?, cvText?} → response {milestones:[{title,focusCriteria[],lessons:[{title}]}]}.
public class AiServiceRoadmapGenerator : IAiServiceRoadmapGenerator
{
    private readonly HttpClient _httpClient;
    private readonly string? _token;
    private readonly ILogger<AiServiceRoadmapGenerator> _logger;

    private static readonly JsonSerializerOptions Json = new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        PropertyNameCaseInsensitive = true
    };

    public AiServiceRoadmapGenerator(
        HttpClient httpClient, IConfiguration config, ILogger<AiServiceRoadmapGenerator> logger)
    {
        _httpClient = httpClient;
        // GEN-7: cả 3 endpoint class này gọi (/generate-roadmap · /generate-lesson-theory ·
        // /summarize-roadmap) nay gate X-Internal-Token (fail-closed) → đính token.
        _token = config["Internal:Token"];
        _logger = logger;
    }

    /// <summary>
    /// POST tới AIService kèm X-Internal-Token (GEN-7). Class này gọi 3 endpoint với cùng hình dạng
    /// request, nên gom một chỗ: call site mới về sau tự mang token thay vì phải nhớ đính tay
    /// (dùng thẳng PostAsJsonAsync sẽ bỏ header và chỉ hỏng lúc chạy thật, không test nào kêu).
    /// </summary>
    private async Task<HttpResponseMessage> PostInternalAsync(string path, object payload, CancellationToken ct)
    {
        // PHẢI await trong hàm: trả thẳng Task sẽ để `using` dispose request (kèm Content) TRƯỚC khi
        // gửi xong → ObjectDisposedException lúc chạy thật.
        using var request = new HttpRequestMessage(HttpMethod.Post, path)
        {
            Content = JsonContent.Create(payload)
        };
        request.Headers.TryAddWithoutValidation("X-Internal-Token", _token);
        return await _httpClient.SendAsync(request, ct);
    }

    // Shape res AIService — chỉ cấu trúc (không điểm).
    // MIS1-B5 — MistakeIds: mistake_key model gán khi gom chủ đề (MIS1-B2). CHƯA lọc theo id thật ở
    // đây — System.Text.Json sẽ VỨT field này ngay lúc deserialize nếu thiếu khai, nên phải khai ở
    // CẢ HAI record (milestone lẫn lesson), y hệt bẫy `focusCriteria`/BC14 đã cắn repo nhiều lần.
    private record RoadmapApiResponse(List<MilestoneApi>? Milestones);
    private record MilestoneApi(
        string? Title, List<string>? FocusCriteria, List<LessonApi>? Lessons,
        List<string>? MistakeIds);
    private record LessonApi(string? Title, List<string>? MistakeIds);

    // MIS1-B5 — trần độ dài đo phân vị 90 trên production, KHÔNG phải phỏng đoán.
    private const int MistakeQuestionMaxChars = 260;
    private const int MistakeReasoningMaxChars = 350;
    private static string Truncate(string s, int max) => s.Length <= max ? s : s[..max];

    public async Task<RoadmapGenAiResult> GenerateAsync(
        string jobCategory, string level,
        IReadOnlyList<RoadmapWeakness>? weaknesses,
        string? focus, string? cvAnalysisSummary, string? priorRoadmapSummary,
        IReadOnlyList<QuestionTargetCriterionDto>? criteria = null,
        string scope = "Standard",
        IReadOnlyList<CriterionEvidence>? evidence = null,
        RoadmapMode mode = RoadmapMode.LevelUp,
        string? currentLevel = null,
        CancellationToken ct = default,
        IReadOnlyList<RoadmapMistake>? mistakes = null)
        => await GenerateAsync(jobCategory, level, weaknesses, focus, cvAnalysisSummary, priorRoadmapSummary, ct, "vi", criteria, scope, evidence, mode, currentLevel, mistakes);

    public async Task<RoadmapGenAiResult> GenerateAsync(string jobCategory, string level, IReadOnlyList<RoadmapWeakness>? weaknesses, string? focus, string? cvAnalysisSummary, string? priorRoadmapSummary, CancellationToken ct, string language, IReadOnlyList<QuestionTargetCriterionDto>? criteria = null, string scope = "Standard", IReadOnlyList<CriterionEvidence>? evidence = null, RoadmapMode mode = RoadmapMode.LevelUp, string? currentLevel = null, IReadOnlyList<RoadmapMistake>? mistakes = null)
    {
        var payload = new
        {
            jobCategory,
            language,
            level,
            // rỗng/null → AI sinh roadmap chuẩn theo level (schema WeaknessScore: criterionName + percentage).
            weaknesses = weaknesses?.Select(w => new { criterionName = w.CriterionName, percentage = w.Percentage }),
            // 🔴 `cvText` ĐÃ BỊ GỠ khỏi payload — đừng nối lại; lý do đầy đủ ở
            // IAiServiceRoadmapGenerator. Đo được là CV thô không tác động gì lên cấu trúc roadmap.
            // Trình độ HIỆN TẠI suy từ CV (khác `level` = MỤC TIÊU). Khoá RIÊNG chứ không nhúng
            // vào `cvAnalysisSummary`: chuỗi đó vào prompt dưới nhãn DỮ LIỆU, còn đây là CHỈ THỊ.
            // ⚠ AIService khai `currentLevel: str | None` tường minh — thiếu dòng khai đó thì
            // `extra='ignore'` NUỐT IM LẶNG (bẫy đã cắn repo 4 lần); có test khoá hai đầu.
            currentLevel,
            // BC17 — ngữ cảnh thêm do candidate chọn (đều null → hành vi cũ). Worker Python khai đúng 3 field
            // camelCase này (extra='ignore' sẽ nuốt im lặng nếu lệch tên) và tự bọc như DỮ LIỆU (AI-4).
            focus,
            cvAnalysisSummary,
            priorRoadmapSummary,
            // BE-1 — tiêu chí năng lực THẬT để milestone.focusCriteria chọn NGUYÊN VĂN thay vì bịa tên.
            // Anonymous object viết tay tên trường camelCase — mẫu `criteria` của AiServiceQuestionGenerator:
            // JsonContent.Create dùng JsonSerializerDefaults.Web (camelCase) nên tên TRƯỜNG không phải rủi ro
            // thật, nhưng ĐỔI TÊN trường thì pydantic extra='ignore' vẫn nuốt im lặng — giữ nguyên mẫu cho nhất quán.
            criteria = criteria is { Count: > 0 }
                ? criteria.Select(c => new { criterionId = c.CriterionId, name = c.Name })
                : null,
            // BE-4 — độ dài roadmap ("Quick"/"Standard"). AIService pydantic schema khai `scope: str =
            // "Standard"` tường minh (cùng bẫy extra='ignore' nêu ở `criteria`) nên luôn gửi, không để null.
            scope,
            // 🔴 MIS1-B5 — CẤM: khoá `evidence` KHÔNG còn xuất hiện trong payload (không phải chỉ
            // luôn gửi `null`). `JsonContent.Create` dùng `JsonSerializerDefaults.Web`
            // (DefaultIgnoreCondition = Never) nên MỘT property `evidence = null` trong anonymous
            // type vẫn ra `"evidence":null` trên dây — phải BỎ HẲN property này, không phải gán null.
            // Tham số `evidence` của HÀM này vẫn GIỮ (interface/call site cũ không vỡ chữ ký) —
            // chỉ đơn giản KHÔNG còn được chiếu vào payload. `mistakes` ngay dưới đây thay thế nó
            // làm nguồn GOM CHỦ ĐỀ (MIS1-B2).
            //
            // Chế độ lộ trình — gửi dạng CHUỖI ("LevelUp"/"Reinforce") khớp `app.roadmap_mode`.
            // AIService khai `mode: str = "LevelUp"` tường minh trong pydantic schema; thiếu dòng
            // khai đó thì `extra='ignore'` NUỐT IM LẶNG và mọi lộ trình ôn tập được sinh như
            // LevelUp mà không lỗi ở đâu cả (bẫy đã cắn repo 4 lần) — có test khoá hai đầu.
            mode = mode.ToString(),
            // MIS1-B5 — LỖI SAI làm nguồn GOM CHỦ ĐỀ (MIS1-B2). 5 trường — KHÔNG answer, KHÔNG
            // sampleAnswer (roadmap chỉ cần đủ để gom, không cần nguyên văn câu trả lời/đáp án mẫu
            // — mẫu docstring `app.schemas.RoadmapMistake`). id = mistake_key (.NET MINT, không
            // phải model tự đặt) để filter_milestone_mistakes lọc CHÍNH XÁC phía AIService.
            mistakes = mistakes is { Count: > 0 }
                ? mistakes.Select(m => new
                  {
                      id = m.MistakeKey,
                      criterionName = m.CriterionName,
                      scorePct = (int)Math.Round(m.ScorePct),
                      question = Truncate(m.Question, MistakeQuestionMaxChars),
                      reasoning = Truncate(m.Reasoning, MistakeReasoningMaxChars),
                  })
                : null,
        };

        HttpResponseMessage response;
        try
        {
            response = await PostInternalAsync("/api/v1/generate-roadmap", payload, ct);
        }
        catch (Exception ex) when (ex is HttpRequestException or TaskCanceledException)
        {
            _logger.LogError(ex, "Không gọi được AIService /generate-roadmap");
            throw new AiServiceException("Không gọi được AIService /generate-roadmap", ex);
        }

        if (!response.IsSuccessStatusCode)
        {
            var error = await response.Content.ReadAsStringAsync(ct);
            _logger.LogError("AIService /generate-roadmap lỗi: {StatusCode} - {Error}", response.StatusCode, error);
            throw new AiServiceException($"AIService /generate-roadmap trả {(int)response.StatusCode}");
        }

        RoadmapApiResponse? body;
        try
        {
            body = await response.Content.ReadFromJsonAsync<RoadmapApiResponse>(Json, ct);
        }
        catch (JsonException ex)
        {
            _logger.LogError(ex, "AIService /generate-roadmap trả JSON không hợp lệ");
            throw new AiServiceException("AIService /generate-roadmap trả JSON không hợp lệ", ex);
        }

        if (body?.Milestones is null || body.Milestones.Count == 0)
            throw new AiServiceException("AIService /generate-roadmap trả rỗng");

        // MIS1-B5 — MistakeIds đi THẲNG, CHƯA lọc theo id thật (CẤM: tin thẳng AI) — narrow là việc
        // của RoadmapService.CreateAsync (nó mới biết tập id ĐÃ CẤP thật sự cho lượt gọi này).
        var milestones = body.Milestones.Select(m => new GeneratedMilestone(
            m.Title ?? string.Empty,
            m.FocusCriteria ?? [],
            (m.Lessons ?? []).Select(l => new GeneratedLesson(l.Title ?? string.Empty, l.MistakeIds)).ToList(),
            m.MistakeIds
        )).ToList();

        return new RoadmapGenAiResult(milestones);
    }

    // Shape res AIService /generate-lesson-theory (GenerateLessonTheoryResponse):
    // markdown + F15 tài liệu học (đã sanitize allowlist tên miền phía AIService) + RAG citedChunkIds
    // + MIS1-B5 mistakeReview (CHƯA lọc theo id thật — narrow ở RoadmapLessonService.OpenLessonAsync).
    private record LessonTheoryApiResponse(
        string? TheoryMarkdown, List<LessonResourceApi>? Resources, List<string>? CitedChunkIds,
        List<MistakeReviewApi>? MistakeReview);
    private record LessonResourceApi(string? Title, string? Type, string? Publisher, string? Url);
    private record MistakeReviewApi(string? MistakeId, string? WhatWentWrong, string? HowToFixIt);

    // MIS1-B5 — trần độ dài đo phân vị 90 trên production, KHÔNG phải phỏng đoán.
    private const int LessonMistakeQuestionMaxChars = 260;
    private const int LessonMistakeAnswerMaxChars = 400;
    private const int LessonMistakeReasoningMaxChars = 350;
    private const int LessonMistakeSampleAnswerMaxChars = 700;

    // BC14 — POST /generate-lesson-theory {jobCategory, level, lessonTitle, focusCriteria[], weaknesses?}
    // → {theoryMarkdown}. Sync như /generate-roadmap. Lỗi → AiServiceException (→ 502).
    // RAG grounding — thêm grounding[] (Contract 2) → đọc citedChunkIds.
    public async Task<LessonTheoryResult> GenerateLessonTheoryAsync(
        string jobCategory, string level, string lessonTitle,
        IReadOnlyList<string> focusCriteria, IReadOnlyList<string>? weaknesses,
        IReadOnlyList<GroundingChunk>? grounding = null,
        IReadOnlyList<CriterionEvidence>? evidence = null,
        RoadmapMode mode = RoadmapMode.LevelUp,
        CancellationToken ct = default,
        IReadOnlyList<RoadmapMistake>? mistakes = null)
        => await GenerateLessonTheoryAsync(jobCategory, level, lessonTitle, focusCriteria, weaknesses, grounding, ct, "vi", evidence, mode, mistakes);

    public async Task<LessonTheoryResult> GenerateLessonTheoryAsync(string jobCategory, string level, string lessonTitle, IReadOnlyList<string> focusCriteria, IReadOnlyList<string>? weaknesses, IReadOnlyList<GroundingChunk>? grounding, CancellationToken ct, string language, IReadOnlyList<CriterionEvidence>? evidence = null, RoadmapMode mode = RoadmapMode.LevelUp, IReadOnlyList<RoadmapMistake>? mistakes = null)
    {
        var payload = new
        {
            jobCategory,
            language,
            level,
            lessonTitle,
            focusCriteria = focusCriteria ?? [],
            weaknesses,
            // RAG grounding — snapshot precompute (roadmap_lessons.grounding_refs). null → sinh ungrounded.
            grounding = grounding is { Count: > 0 }
                ? grounding.Select(g => new { chunkId = g.ChunkId, content = g.Content, sourceUrl = g.SourceUrl, sourceTitle = g.SourceTitle })
                : null,
            // 🔴 MIS1-B5 — CẤM: khoá `evidence` KHÔNG còn xuất hiện trong payload (cùng lý do
            // `JsonContent.Create`/`DefaultIgnoreCondition=Never` như ở `GenerateAsync` phía trên —
            // xoá hẳn property, không gán null). Tham số hàm vẫn giữ nguyên chữ ký.
            //
            // Chế độ ôn tập đổi TRỌNG TÂM bài giảng (giải thích vì sao lần trước sai) — cùng hợp
            // đồng chuỗi + cùng bẫy `extra='ignore'` như ở `/generate-roadmap` ngay trên.
            mode = mode.ToString(),
            // MIS1-B5 — ≤3 lỗi ĐÚNG bài này, anchor bài giảng + nguồn mistakeReview (MIS1-B3). 6
            // trường ĐỦ (kể cả answer/sampleAnswer — bài học cần NGUYÊN VĂN để giải thích sai ở
            // đâu), KHÔNG như /generate-roadmap chỉ cần đủ để gom chủ đề.
            mistakes = mistakes is { Count: > 0 }
                ? mistakes.Select(m => new
                  {
                      id = m.MistakeKey,
                      criterionName = m.CriterionName,
                      question = Truncate(m.Question, LessonMistakeQuestionMaxChars),
                      answer = Truncate(m.Answer, LessonMistakeAnswerMaxChars),
                      reasoning = Truncate(m.Reasoning, LessonMistakeReasoningMaxChars),
                      sampleAnswer = m.SampleAnswer is null
                          ? null
                          : Truncate(m.SampleAnswer, LessonMistakeSampleAnswerMaxChars),
                  })
                : null,
        };

        HttpResponseMessage response;
        try
        {
            response = await PostInternalAsync("/api/v1/generate-lesson-theory", payload, ct);
        }
        catch (Exception ex) when (ex is HttpRequestException or TaskCanceledException)
        {
            _logger.LogError(ex, "Không gọi được AIService /generate-lesson-theory");
            throw new AiServiceException("Không gọi được AIService /generate-lesson-theory", ex);
        }

        if (!response.IsSuccessStatusCode)
        {
            var error = await response.Content.ReadAsStringAsync(ct);
            _logger.LogError("AIService /generate-lesson-theory lỗi: {StatusCode} - {Error}", response.StatusCode, error);
            throw new AiServiceException($"AIService /generate-lesson-theory trả {(int)response.StatusCode}");
        }

        LessonTheoryApiResponse? body;
        try
        {
            body = await response.Content.ReadFromJsonAsync<LessonTheoryApiResponse>(Json, ct);
        }
        catch (JsonException ex)
        {
            _logger.LogError(ex, "AIService /generate-lesson-theory trả JSON không hợp lệ");
            throw new AiServiceException("AIService /generate-lesson-theory trả JSON không hợp lệ", ex);
        }

        if (body is null || string.IsNullOrWhiteSpace(body.TheoryMarkdown))
            throw new AiServiceException("AIService /generate-lesson-theory trả rỗng");

        // F15 — resources RỖNG KHÔNG phải lỗi (lý thuyết vẫn dùng được), khác theoryMarkdown rỗng.
        // Bỏ mục thiếu title; url giữ nguyên những gì AIService đã lọc qua allowlist tên miền.
        var resources = (body.Resources ?? [])
            .Where(r => !string.IsNullOrWhiteSpace(r.Title))
            .Select(r => new LessonResource(
                r.Title!.Trim(),
                string.IsNullOrWhiteSpace(r.Type) ? "Doc" : r.Type!.Trim(),
                string.IsNullOrWhiteSpace(r.Publisher) ? null : r.Publisher!.Trim(),
                string.IsNullOrWhiteSpace(r.Url) ? null : r.Url!.Trim()))
            .ToList();

        // MIS1-B5 — mistakeReview đi THẲNG, CHƯA lọc theo id thật (CẤM: tin thẳng AI) và CHƯA đòi
        // ruột whatWentWrong/howToFixIt — narrow là việc của RoadmapLessonService.OpenLessonAsync
        // (nó mới biết tập id ĐÃ CẤP thật sự cho lượt gọi này). Mục thiếu id/what/how bị bỏ ở đây
        // (biên nhận hỏng, mẫu lọc resources ngay trên) — không đáng làm hỏng cả danh sách.
        var mistakeReview = body.MistakeReview is null
            ? null
            : body.MistakeReview
                .Where(r => !string.IsNullOrWhiteSpace(r.MistakeId)
                            && !string.IsNullOrWhiteSpace(r.WhatWentWrong)
                            && !string.IsNullOrWhiteSpace(r.HowToFixIt))
                .Select(r => new LessonMistakeReviewItem(r.MistakeId!, r.WhatWentWrong!, r.HowToFixIt!))
                .ToList();

        return new LessonTheoryResult(body.TheoryMarkdown, resources, body.CitedChunkIds, mistakeReview);
    }

    // Shape res AIService /summarize-roadmap — kết luận chi tiết + nhận xét chung.
    private record SummarizeRoadmapApiResponse(
        List<string>? Strengths, List<string>? Weaknesses, List<string>? Improvements, string? OverallComment);

    // BC15 — POST /summarize-roadmap {jobCategory, level, criteriaProgress:[{criterionName, startPct?, endPct,
    // levelThreshold, passed}]} → {strengths[], weaknesses[], improvements[], overallComment}. Sync như các call kia.
    // Lỗi transport/status/JSON → AiServiceException (caller best-effort nuốt). Body 200 nhưng rỗng → trả list rỗng
    // + comment null (KHÔNG ném) để roadmap vẫn Completed với kết luận rỗng.
    public async Task<RoadmapSummaryAiResult> SummarizeRoadmapAsync(
        string jobCategory, string level,
        IReadOnlyList<RoadmapCriteriaProgress> criteriaProgress,
        CancellationToken ct = default)
        => await SummarizeRoadmapAsync(jobCategory, level, criteriaProgress, ct, "vi");

    public async Task<RoadmapSummaryAiResult> SummarizeRoadmapAsync(string jobCategory, string level, IReadOnlyList<RoadmapCriteriaProgress> criteriaProgress, CancellationToken ct, string language)
    {
        var payload = new
        {
            jobCategory,
            language,
            level,
            criteriaProgress = criteriaProgress.Select(c => new
            {
                criterionName = c.CriterionName,
                startPct = c.StartPct,
                endPct = c.EndPct,
                levelThreshold = c.LevelThreshold,
                passed = c.Passed
            })
        };

        HttpResponseMessage response;
        try
        {
            response = await PostInternalAsync("/api/v1/summarize-roadmap", payload, ct);
        }
        catch (Exception ex) when (ex is HttpRequestException or TaskCanceledException)
        {
            _logger.LogError(ex, "Không gọi được AIService /summarize-roadmap");
            throw new AiServiceException("Không gọi được AIService /summarize-roadmap", ex);
        }

        if (!response.IsSuccessStatusCode)
        {
            var error = await response.Content.ReadAsStringAsync(ct);
            _logger.LogError("AIService /summarize-roadmap lỗi: {StatusCode} - {Error}", response.StatusCode, error);
            throw new AiServiceException($"AIService /summarize-roadmap trả {(int)response.StatusCode}");
        }

        SummarizeRoadmapApiResponse? body;
        try
        {
            body = await response.Content.ReadFromJsonAsync<SummarizeRoadmapApiResponse>(Json, ct);
        }
        catch (JsonException ex)
        {
            _logger.LogError(ex, "AIService /summarize-roadmap trả JSON không hợp lệ");
            throw new AiServiceException("AIService /summarize-roadmap trả JSON không hợp lệ", ex);
        }

        return new RoadmapSummaryAiResult(
            body?.Strengths ?? [],
            body?.Weaknesses ?? [],
            body?.Improvements ?? [],
            string.IsNullOrWhiteSpace(body?.OverallComment) ? null : body!.OverallComment);
    }
}
