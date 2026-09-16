using System.Net.Http.Headers;
using System.Text.Json;
using Isas.InterviewService.DTOs;

namespace Isas.InterviewService.Services;

/// <summary>Kết quả chép lời một bản ghi rời (không gắn buổi luyện nào).</summary>
/// <param name="Text">Bản chép lời; rỗng khi <paramref name="RejectReason"/> = <c>no_speech</c>.</param>
/// <param name="DeliveryMetrics">Số đo cách nói (F11) — <c>null</c> khi không đo được.</param>
/// <param name="TranscriptEngine">Engine đã thật sự chép (whisper-1 / gemini / local:small…).</param>
/// <param name="RejectReason"><c>no_speech</c> = cổng VAD không thấy tiếng nói; <c>null</c> = bình thường.</param>
public record TranscribeResult(
    string Text, DeliveryMetricsDto? DeliveryMetrics, string? TranscriptEngine, string? RejectReason);

public interface IAiServiceTranscriber
{
    Task<TranscribeResult> TranscribeAsync(
        Stream audio, string fileName, string contentType, string language, CancellationToken ct = default);
}

/// <summary>
/// Gọi AIService <c>POST /api/v1/transcribe</c> (multipart) cho màn admin "tự thử thước đo": người
/// dùng nói vào mic, hệ chép lời + đo cách nói, rồi bản chép được chấm bằng đúng bộ chấm thật.
///
/// <para>FE không gọi thẳng AIService được — endpoint đó gate <c>X-Internal-Token</c> (GEN-7) và
/// AIService không lộ qua gateway ở production. Client này là cầu duy nhất. Không lưu gì (không
/// phải buổi luyện, không answer, không S3) — bản ghi chỉ đi qua bộ nhớ.</para>
/// </summary>
public class AiServiceTranscribeClient(
    HttpClient http, IConfiguration config, ILogger<AiServiceTranscribeClient> logger) : IAiServiceTranscriber
{
    private static readonly JsonSerializerOptions Json = new(JsonSerializerDefaults.Web);
    private readonly string? _internalToken = config["Internal:Token"];

    public async Task<TranscribeResult> TranscribeAsync(
        Stream audio, string fileName, string contentType, string language, CancellationToken ct = default)
    {
        using var form = new MultipartFormDataContent();
        var part = new StreamContent(audio);
        part.Headers.ContentType = MediaTypeHeaderValue.Parse(
            string.IsNullOrWhiteSpace(contentType) ? "application/octet-stream" : contentType);
        form.Add(part, "file", fileName);

        using var msg = new HttpRequestMessage(
            HttpMethod.Post, $"/api/v1/transcribe?language={Uri.EscapeDataString(language)}") { Content = form };
        if (!string.IsNullOrWhiteSpace(_internalToken))
            msg.Headers.TryAddWithoutValidation("X-Internal-Token", _internalToken);

        HttpResponseMessage resp;
        try
        {
            resp = await http.SendAsync(msg, ct);
        }
        catch (Exception ex) when (ex is HttpRequestException or TaskCanceledException)
        {
            throw new DownstreamServiceException("Không gọi được AIService /transcribe", ex);
        }

        if (!resp.IsSuccessStatusCode)
        {
            var error = await resp.Content.ReadAsStringAsync(ct);
            logger.LogError("AIService /transcribe → {Status}: {Error}", resp.StatusCode, error);
            throw new DownstreamServiceException($"AIService /transcribe trả {(int)resp.StatusCode}");
        }

        ResponseDto? body;
        try
        {
            body = await resp.Content.ReadFromJsonAsync<ResponseDto>(Json, ct);
        }
        catch (JsonException ex)
        {
            throw new DownstreamServiceException("AIService /transcribe trả JSON không đọc được", ex);
        }
        if (body is null) throw new DownstreamServiceException("AIService /transcribe trả body rỗng");

        return new TranscribeResult(body.Text ?? "", body.DeliveryMetrics, body.TranscriptEngine, body.RejectReason);
    }

    // Khoá JSON là HỢP ĐỒNG với `main.py::transcribe` — đổi tên bên Python mà quên bên này thì field
    // rụng im lặng (lớp bug `focusCriteria`/`metricsVersion`). Test hợp đồng đọc thẳng file Python.
    private sealed record ResponseDto(
        string? Text, DeliveryMetricsDto? DeliveryMetrics, string? TranscriptEngine, string? RejectReason);
}
