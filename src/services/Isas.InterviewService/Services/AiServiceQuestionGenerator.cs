using System.Net.Http.Json;
using System.Text.Json;
using Isas.InterviewService.DTOs;
using Isas.InterviewService.Services.Interfaces;

namespace Isas.InterviewService.Services;

public class AiServiceQuestionGenerator : IAiServiceQuestionGenerator
{
    private readonly HttpClient _httpClient;
    private readonly IConfiguration _config;
    private readonly ILogger<AiServiceQuestionGenerator> _logger;
    private readonly string? _token;

    public AiServiceQuestionGenerator(
        HttpClient httpClient, IConfiguration config, ILogger<AiServiceQuestionGenerator> logger)
    {
        _httpClient = httpClient;
        _config = config;
        _logger = logger;
        _token = config["Internal:Token"];
    }

    private static readonly JsonSerializerOptions Json = new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        PropertyNameCaseInsensitive = true
    };

    // Contract 2 — questions GIỮ NGUYÊN (Campaign B2B còn gọi); citations ADDITIVE (chỉ có khi truyền grounding).
    // targetCriteria ADDITIVE: mảng SONG SONG theo index với `questions`, phần tử i = danh sách
    // criterionId (string GUID) mà `questions[i]` nhắm tới; có thể rỗng (AIService fail-open).
    private record FastAPIQuestionsResponse(
        List<string>? Questions, List<CitationApi>? Citations, List<List<string>?>? TargetCriteria);
    private record CitationApi(int QuestionIndex, List<string>? CitedChunkIds);

    public Task<List<GeneratedQuestion>> GenerateQuestionsAsync(
        string jobCategory, string? cvText, string? jdText,
        string seniority = "Junior", CancellationToken ct = default)
        => GenerateQuestionsAsync(
            jobCategory, cvText, jdText, focusCriteria: null, count: null, seniority, ct);

    // BC14 (focusCriteria) + F2b (count). null = không ghi đè → AIService dùng mặc định của nó.
    public async Task<List<GeneratedQuestion>> GenerateQuestionsAsync(
        string jobCategory, string? cvText, string? jdText,
        IReadOnlyList<string>? focusCriteria, int? count,
        string seniority = "Junior", CancellationToken ct = default)
    {
        var result = await GenerateQuestionsAsync(
            jobCategory, cvText, jdText, focusCriteria, count, grounding: null, "vi",
            criteria: null, seniority, ct);
        return result.Questions;
    }

    // RAG grounding — overload GROUNDED. Xem ghi chú ở interface: overload này KHÔNG mang `seniority`
    // (đụng độ chữ ký với overload `language` ngay dưới) ⇒ luôn gửi mặc định.
    public async Task<GeneratedQuestionsResult> GenerateQuestionsAsync(
        string jobCategory, string? cvText, string? jdText,
        IReadOnlyList<string>? focusCriteria, int? count,
        IReadOnlyList<GroundingChunk>? grounding, CancellationToken ct = default)
        => await GenerateQuestionsAsync(
            jobCategory, cvText, jdText, focusCriteria, count, grounding, "vi",
            criteria: null, "Junior", ct);

    public async Task<GeneratedQuestionsResult> GenerateQuestionsAsync(
        string jobCategory, string? cvText, string? jdText,
        IReadOnlyList<string>? focusCriteria, int? count,
        IReadOnlyList<GroundingChunk>? grounding, string language,
        string seniority = "Junior", CancellationToken ct = default)
        => await GenerateQuestionsAsync(
            jobCategory, cvText, jdText, focusCriteria, count, grounding, language,
            criteria: null, seniority, ct);

    public async Task<GeneratedQuestionsResult> GenerateQuestionsAsync(
        string jobCategory, string? cvText, string? jdText,
        IReadOnlyList<string>? focusCriteria, int? count,
        IReadOnlyList<GroundingChunk>? grounding, string language,
        IReadOnlyList<QuestionTargetCriterionDto>? criteria,
        string seniority = "Junior", CancellationToken ct = default)
    {
        var payload = new
        {
            jobCategory,
            cvText,
            jdText,
            // ⚠ Field TỪNG bị AIService nuốt im lặng (pydantic extra='ignore') — đã khai ở schema (F2b/W1).
            focusCriteria = focusCriteria is { Count: > 0 } ? focusCriteria : null,
            count,
            language,
            // RAG grounding — chunk truy hồi (Contract 2). Chỉ gửi khi có → AIService chèn block "TÀI LIỆU
            // THAM CHIẾU UY TÍN" + trả citations. null → sinh ungrounded như cũ (Campaign B2B không truyền).
            grounding = grounding is { Count: > 0 }
                ? grounding.Select(g => new { chunkId = g.ChunkId, content = g.Content, sourceUrl = g.SourceUrl, sourceTitle = g.SourceTitle })
                : null,
            // Tiêu chí NỘI DUNG để AIService gắn nhãn từng câu. Vắng/rỗng → gửi null ⇒ AIService KHÔNG
            // gắn nhãn (hành vi cũ). Anonymous object viết tay tên trường camelCase — CÙNG LÝ DO như
            // `grounding` ngay trên: JsonContent.Create không áp naming policy, dùng record .NET ở đây
            // sẽ serialize ra `CriterionId`/`Name` và Python im lặng bỏ qua.
            criteria = criteria is { Count: > 0 }
                ? criteria.Select(c => new { criterionId = c.CriterionId, name = c.Name })
                : null,
            // SEN1 — cấp độ ứng viên. Tên thành viên ở đây là nơi DUY NHẤT quyết định tên khoá ra dây,
            // và hợp đồng với pydantic là `seniority`.
            //
            // ⚠ Đính chính một hiểu nhầm đang lưu hành trong repo (đã probe thật, không suy luận):
            // `JsonContent.Create(payload)` KHÔNG dùng `JsonSerializerOptions.Default` mà dùng
            // `JsonSerializerDefaults.Web` ⇒ CÓ áp camelCase. Viết `Seniority` ở đây vẫn ra
            // `"seniority"`. Nên rủi ro thật KHÔNG phải hoa/thường mà là **đổi TÊN** (vd
            // `seniorityLevel`) — đổi tên thì pydantic `extra='ignore'` nuốt im lặng, không lỗi,
            // không log. *(Ngược lại, `JsonSerializer.Serialize(job)` không truyền options — như ở
            // `ScoringJobPublisher` — mới thật sự ra PascalCase.)*
            //
            // Không bao giờ để `null` ra dây: `GenerateQuestionsRequest.seniority` bên Python khai
            // `str` (không Optional) ⇒ `"seniority": null` là **422**, mà đường sinh câu hỏi nằm SAU
            // `ReserveAsync` ⇒ một giá trị rỗng lọt xuống đây sẽ thành buổi hỏng ĐÃ TRỪ CREDIT
            // (PAY-5). Giá trị lạ NHƯNG khác rỗng thì cứ gửi — AIService tự hạ về "Junior" và ghi
            // log, đúng chỗ để phát hiện caller gửi sai.
            seniority = string.IsNullOrWhiteSpace(seniority) ? "Junior" : seniority
        };

        // RAG grounding — /generate-questions là endpoint AIService (GEN-1/GEN-7 internal-only) → gắn
        // X-Internal-Token. TRƯỚC ĐÂY THIẾU (chỉ chạy được vì AIService chưa gate endpoint sinh); thêm để
        // khớp fail-closed khi W1 gate /generate-questions.
        using var request = new HttpRequestMessage(HttpMethod.Post, "/api/v1/generate-questions")
        {
            Content = JsonContent.Create(payload)
        };
        request.Headers.TryAddWithoutValidation("X-Internal-Token", _token);

        HttpResponseMessage response;
        try
        {
            response = await _httpClient.SendAsync(request, ct);
        }
        catch (Exception ex) when (ex is HttpRequestException or TaskCanceledException)
        {
            // Upstream không gọi được (transport/timeout) = AIService lỗi → AiServiceException để controller
            // map 502 (không nuốt thành 400).
            _logger.LogError(ex, "Không gọi được AIService /generate-questions");
            throw new AiServiceException("Không gọi được AIService /generate-questions", ex);
        }

        if (!response.IsSuccessStatusCode)
        {
            var error = await response.Content.ReadAsStringAsync(ct);
            _logger.LogError("FastAPI Error: {StatusCode} - {Error}", response.StatusCode, error);
            throw new AiServiceException($"AIService /generate-questions trả {(int)response.StatusCode}");
        }

        var body = await response.Content.ReadFromJsonAsync<FastAPIQuestionsResponse>(Json, ct);

        // Nhãn tiêu chí theo INDEX. GUARD by-construction (mẫu GroundingMapper "drop id lạ"): chỉ nhận
        // id parse được VÀ nằm trong tập tiêu chí CHÍNH TA vừa gửi đi ⇒ AIService không thể bịa ra một
        // tiêu chí không có trong rubric để lái phạm vi chấm.
        var allowedIds = criteria is { Count: > 0 }
            ? criteria.Select(c => c.CriterionId).ToHashSet()
            : null;
        var targets = body?.TargetCriteria;

        var questions = (body?.Questions ?? new List<string>())
            .Select((qText, idx) => new GeneratedQuestion
            {
                Content = qText,
                TargetCriterionIds = ParseTargets(targets, idx, allowedIds)
            })
            .ToList();

        var citations = (body?.Citations ?? new List<CitationApi>())
            .Select(c => new QuestionCitationDto(c.QuestionIndex, c.CitedChunkIds ?? new List<string>()))
            .ToList();

        return new GeneratedQuestionsResult(questions, citations);
    }

    /// <summary>
    /// Nhãn của câu thứ <paramref name="index"/>: parse GUID, bỏ id lạ (không nằm trong
    /// <paramref name="allowedIds"/> = tập ta đã gửi), khử trùng.
    ///
    /// <para>🔑 Giữ ĐÚNG 3 trạng thái (xem <see cref="Entities.PracticeQuestion.TargetCriterionIds"/>):</para>
    /// <list type="bullet">
    ///   <item>AIService không trả mảng / index vượt / phần tử <c>null</c> ⇒ <c>null</c> = CHƯA HỎI.</item>
    ///   <item>Phần tử là <c>[]</c> ⇒ trả <c>[]</c> = ĐÃ HỎI, câu không nhắm tiêu chí nội dung nào
    ///   (câu xã giao) ⇒ chỉ chấm 4 tiêu chí cách nói. KHÔNG được quy về <c>null</c>.</item>
    ///   <item>Phần tử có id nhưng KHÔNG id nào sống sót qua guard ⇒ <c>null</c>, KHÔNG phải <c>[]</c>:
    ///   AIService vừa khẳng định câu này có nhắm tiêu chí, chỉ là nó gọi tên những thứ không thuộc
    ///   rubric ⇒ ta không có tín hiệu đáng tin nào để thu hẹp, và "toàn id lạ" khác hẳn với lời
    ///   khẳng định "không nhắm gì cả".</item>
    /// </list>
    /// </summary>
    private IReadOnlyList<Guid>? ParseTargets(
        List<List<string>?>? targets, int index, HashSet<Guid>? allowedIds)
    {
        if (targets is null || index >= targets.Count) return null;
        var raw = targets[index];
        if (raw is null) return null;
        if (raw.Count == 0) return Array.Empty<Guid>();

        var parsed = new List<Guid>();
        foreach (var s in raw)
        {
            if (!Guid.TryParse(s, out var id)) continue;
            if (allowedIds is not null && !allowedIds.Contains(id)) continue;
            if (!parsed.Contains(id)) parsed.Add(id);
        }

        if (parsed.Count == 0)
        {
            _logger.LogWarning(
                "AIService gắn nhãn câu {Index} bằng {Count} tiêu chí nhưng KHÔNG id nào thuộc rubric đã gửi "
                + "({Raw}) — bỏ nhãn (chấm đủ rubric) thay vì coi như câu không nhắm tiêu chí nào",
                index, raw.Count, string.Join(",", raw));
            return null;
        }

        return parsed;
    }
}
