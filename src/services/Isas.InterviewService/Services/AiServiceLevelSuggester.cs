using System.Net.Http.Json;
using System.Text.Json;

namespace Isas.InterviewService.Services;

/// <summary>Một tiêu chí gửi đi để AI soạn mốc.</summary>
public record LevelSuggestionInput(Guid CriterionId, string Name, string? Description, int MaxScore);

/// <summary>Mốc AI đề xuất cho một tiêu chí.</summary>
public record SuggestedLevelSet(Guid CriterionId, IReadOnlyList<SuggestedLevel> Levels);

public record SuggestedLevel(int Score, string Descriptor);

public interface IAiServiceLevelSuggester
{
    /// <summary>
    /// Gọi AIService <c>POST /api/v1/suggest-criterion-levels</c>. Lỗi → <see cref="DownstreamServiceException"/>
    /// (controller map 502). CỐ Ý KHÔNG có fallback.
    /// </summary>
    Task<IReadOnlyList<SuggestedLevelSet>> SuggestAsync(
        string jobCategory, string language, string? seniority, string? jdText,
        IReadOnlyList<LevelSuggestionInput> criteria, CancellationToken ct = default);
}

/// <summary>
/// AI soạn mốc điểm cho bộ chuẩn B2C (bản sao của client cùng tên bên CampaignService — record thuần,
/// không chạm DB).
///
/// <para><b>KHÔNG fallback dải mặc định.</b> Fallback ở đây nghĩa là admin mở màn hình lên, thấy
/// "Mức 3: Mức 3/5" và tin rằng AI đã soạn nó, rồi lưu một thước đo chưa ai viết cho TOÀN BỘ người
/// luyện tập. "Chưa có mốc" là trạng thái HỢP LỆ (đường chấm dùng dải mặc định như trước) nên
/// fail-loud không chặn ai làm việc.</para>
///
/// <para>Kết quả KHÔNG ghi DB — trả về để admin xem/sửa rồi lưu qua đúng một cửa <c>PUT</c>, giữ luật
/// bump phiên bản ở một chỗ (mẫu CAMP-16).</para>
/// </summary>
public class AiServiceLevelSuggester : IAiServiceLevelSuggester
{
    private readonly HttpClient _http;
    private readonly string? _internalToken;
    private readonly ILogger<AiServiceLevelSuggester> _logger;

    private static readonly JsonSerializerOptions Json = new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        PropertyNameCaseInsensitive = true
    };

    public AiServiceLevelSuggester(
        HttpClient http, IConfiguration config, ILogger<AiServiceLevelSuggester> logger)
    {
        _http = http;
        _internalToken = config["Internal:Token"];   // GEN-7: endpoint AIService gate fail-closed
        _logger = logger;
    }

    public async Task<IReadOnlyList<SuggestedLevelSet>> SuggestAsync(
        string jobCategory, string language, string? seniority, string? jdText,
        IReadOnlyList<LevelSuggestionInput> criteria, CancellationToken ct = default)
    {
        for (var attempt = 1; attempt <= 2; attempt++)
        {
            try
            {
                return await CallAsync(jobCategory, language, seniority, jdText, criteria, ct);
            }
            catch (DownstreamServiceException) when (attempt == 1)
            {
                _logger.LogWarning("AIService /suggest-criterion-levels lỗi lượt 1 — thử lại lần cuối.");
            }
        }
        // Không tới được: lượt 2 hoặc trả kết quả hoặc ném ra ngoài.
        throw new DownstreamServiceException("AIService /suggest-criterion-levels không phản hồi.");
    }

    private async Task<IReadOnlyList<SuggestedLevelSet>> CallAsync(
        string jobCategory, string language, string? seniority, string? jdText,
        IReadOnlyList<LevelSuggestionInput> criteria, CancellationToken ct)
    {
        using var msg = new HttpRequestMessage(HttpMethod.Post, "/api/v1/suggest-criterion-levels")
        {
            Content = JsonContent.Create(new
            {
                jobCategory,
                language,
                seniority,
                jdText,
                criteria = criteria.Select(c => new { c.CriterionId, c.Name, c.Description, c.MaxScore })
            })
        };
        msg.Headers.TryAddWithoutValidation("X-Internal-Token", _internalToken);

        HttpResponseMessage resp;
        try
        {
            resp = await _http.SendAsync(msg, ct);
        }
        catch (Exception ex) when (ex is HttpRequestException or TaskCanceledException)
        {
            throw new DownstreamServiceException("Không gọi được AIService /suggest-criterion-levels", ex);
        }

        if (!resp.IsSuccessStatusCode)
        {
            var error = await resp.Content.ReadAsStringAsync(ct);
            _logger.LogError("AIService /suggest-criterion-levels → {Status}: {Error}", resp.StatusCode, error);
            throw new DownstreamServiceException(
                $"AIService /suggest-criterion-levels trả {(int)resp.StatusCode}");
        }

        ResponseDto? body;
        try
        {
            body = await resp.Content.ReadFromJsonAsync<ResponseDto>(Json, ct);
        }
        catch (JsonException ex)
        {
            throw new DownstreamServiceException("AIService /suggest-criterion-levels trả JSON không hợp lệ", ex);
        }

        if (body?.Criteria is not { Count: > 0 })
            throw new DownstreamServiceException("AIService /suggest-criterion-levels trả rỗng");

        return body.Criteria
            .Select(c => new SuggestedLevelSet(
                c.CriterionId,
                (c.Levels ?? new List<LevelDto>())
                    .Where(l => !string.IsNullOrWhiteSpace(l.Descriptor))
                    .Select(l => new SuggestedLevel(l.Score, l.Descriptor.Trim()))
                    .ToList()))
            .ToList();
    }

    private sealed record ResponseDto(List<CriterionLevelsDto>? Criteria);
    private sealed record CriterionLevelsDto(Guid CriterionId, List<LevelDto>? Levels);
    private sealed record LevelDto(int Score, string Descriptor);
}
