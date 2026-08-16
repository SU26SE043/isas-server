using System.Net.Http.Json;
using System.Text.Json;

namespace Isas.CampaignService.Services
{
    /// <summary>Một tiêu chí gửi đi chấm thử, kèm MỨC KỲ VỌNG do CODE chọn (không phải model tự đặt).</summary>
    public record PreviewCriterionInput(
        Guid CriterionId, string Name, string? Description, int MaxScore, decimal Weight,
        IReadOnlyList<SessionCriterionLevelInput> Levels,
        int ExpectedWeak, int ExpectedGood, int ExpectedExcellent);

    public record PreviewSampleScore(Guid CriterionId, decimal Score, int? LevelMatched, string? Reasoning);

    public record PreviewSample(string Band, string AnswerText, int WordCount,
        IReadOnlyList<PreviewSampleScore> Scores);

    public record RubricPreviewResult(
        IReadOnlyList<PreviewSample> Samples, int? PromptVersion, bool LengthParityWarning);

    public interface IRubricPreviewClient
    {
        Task<RubricPreviewResult> RunAsync(
            string jobCategory, string language, string? seniority,
            string question, string? sampleAnswer, string? customAnswer, int targetWordCount,
            IReadOnlyList<PreviewCriterionInput> criteria, CancellationToken ct = default);
    }

    /// <summary>
    /// CAMP-19 — gọi AIService <c>POST /api/v1/score-preview</c>: sinh 3 bài mẫu rồi chấm CHÍNH CHÚNG
    /// bằng đúng bộ chấm thật.
    ///
    /// <para>Đồng bộ HTTP, KHÔNG qua hàng đợi: đi đường queue thì phải dựng <c>practice_answers</c> giả
    /// để callback có chỗ ghi — tức THÊM sai khác giữa chấm thử và chấm thật chứ không bớt.</para>
    /// </summary>
    public class AiServiceRubricPreviewClient : IRubricPreviewClient
    {
        private readonly HttpClient _http;
        private readonly string? _internalToken;
        private readonly ILogger<AiServiceRubricPreviewClient> _logger;

        private static readonly JsonSerializerOptions Json = new()
        {
            PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
            PropertyNameCaseInsensitive = true
        };

        public AiServiceRubricPreviewClient(
            HttpClient http, IConfiguration config, ILogger<AiServiceRubricPreviewClient> logger)
        {
            _http = http;
            _internalToken = config["Internal:Token"];
            _logger = logger;
        }

        public async Task<RubricPreviewResult> RunAsync(
            string jobCategory, string language, string? seniority,
            string question, string? sampleAnswer, string? customAnswer, int targetWordCount,
            IReadOnlyList<PreviewCriterionInput> criteria, CancellationToken ct = default)
        {
            using var msg = new HttpRequestMessage(HttpMethod.Post, "/api/v1/score-preview")
            {
                Content = JsonContent.Create(new
                {
                    jobCategory,
                    language,
                    seniority,
                    question,
                    sampleAnswer,
                    customAnswer,
                    targetWordCount,
                    criteria = criteria.Select(c => new
                    {
                        c.CriterionId, c.Name, c.Description, c.MaxScore, c.Weight,
                        levels = c.Levels.Select(l => new { l.Score, l.Descriptor }),
                        c.ExpectedWeak, c.ExpectedGood, c.ExpectedExcellent
                    })
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
                throw new DownstreamServiceException("Không gọi được AIService /score-preview", ex);
            }

            if (!resp.IsSuccessStatusCode)
            {
                var error = await resp.Content.ReadAsStringAsync(ct);
                _logger.LogError("AIService /score-preview → {Status}: {Error}", resp.StatusCode, error);
                throw new DownstreamServiceException($"AIService /score-preview trả {(int)resp.StatusCode}");
            }

            ResponseDto? body;
            try
            {
                body = await resp.Content.ReadFromJsonAsync<ResponseDto>(Json, ct);
            }
            catch (JsonException ex)
            {
                throw new DownstreamServiceException("AIService /score-preview trả JSON không hợp lệ", ex);
            }

            if (body?.Samples is not { Count: > 0 })
                throw new DownstreamServiceException("AIService /score-preview trả rỗng");

            return new RubricPreviewResult(
                body.Samples.Select(s => new PreviewSample(
                    s.Band, s.AnswerText, s.WordCount,
                    (s.Scores ?? new List<ScoreDto>())
                        .Select(x => new PreviewSampleScore(x.CriterionId, x.Score, x.LevelMatched, x.Reasoning))
                        .ToList())).ToList(),
                body.PromptVersion,
                body.LengthParityWarning);
        }

        private sealed record ResponseDto(
            List<SampleDto>? Samples, int? PromptVersion, bool LengthParityWarning);
        private sealed record SampleDto(string Band, string AnswerText, int WordCount, List<ScoreDto>? Scores);
        private sealed record ScoreDto(Guid CriterionId, decimal Score, int? LevelMatched, string? Reasoning);
    }
}
