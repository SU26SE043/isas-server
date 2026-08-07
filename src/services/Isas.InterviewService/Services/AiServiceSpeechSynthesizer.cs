using System.Net.Http.Json;
using Isas.InterviewService.DTOs;
using Isas.InterviewService.Services.Interfaces;

namespace Isas.InterviewService.Services;

// TTS đọc câu hỏi — gọi AIService `/tts` (máy-máy, X-Internal-Token). Nhái mẫu
// AiServiceInterviewDecider. Lỗi transport/non-2xx → AiServiceException → controller map 502;
// FE degrade về chỉ hiện chữ, luồng phỏng vấn KHÔNG bị chặn.
public class AiServiceSpeechSynthesizer : IAiServiceSpeechSynthesizer
{
    private readonly HttpClient _httpClient;
    private readonly string? _internalToken;
    private readonly ILogger<AiServiceSpeechSynthesizer> _logger;

    // Hợp đồng đã chốt với FE. AIService luôn trả mp3; nếu vì lý do gì đó thiếu header thì
    // vẫn khai audio/mpeg để FE phát được (không đoán bừa kiểu khác).
    private const string Mp3ContentType = "audio/mpeg";

    public AiServiceSpeechSynthesizer(
        HttpClient httpClient, IConfiguration config, ILogger<AiServiceSpeechSynthesizer> logger)
    {
        _httpClient = httpClient;
        _internalToken = config["Internal:Token"];   // /tts gate bằng X-Internal-Token (GEN-7)
        _logger = logger;
    }

    public async Task<QuestionSpeech> SynthesizeAsync(string text, CancellationToken ct = default)
        => await SynthesizeAsync(text, "vi", ct);

    public async Task<QuestionSpeech> SynthesizeAsync(string text, string language, CancellationToken ct = default)
    {
        // Chỉ gửi NỘI DUNG câu hỏi. Giọng/ngôn ngữ do AIService quyết (hằng phía server) —
        // Interview không cần biết, và client càng không được chọn.
        using var msg = new HttpRequestMessage(HttpMethod.Post, "/api/v1/tts")
        {
            Content = JsonContent.Create(new { text, language })
        };
        msg.Headers.TryAddWithoutValidation("X-Internal-Token", _internalToken);

        HttpResponseMessage response;
        try
        {
            response = await _httpClient.SendAsync(msg, ct);
        }
        catch (Exception ex) when (ex is HttpRequestException or TaskCanceledException)
        {
            _logger.LogError(ex, "Không gọi được AIService /tts");
            throw new AiServiceException("Không gọi được AIService /tts", ex);
        }

        using (response)
        {
            if (!response.IsSuccessStatusCode)
            {
                var error = await response.Content.ReadAsStringAsync(ct);
                _logger.LogError("AIService /tts lỗi: {StatusCode} - {Error}",
                    response.StatusCode, error);
                throw new AiServiceException($"AIService /tts trả {(int)response.StatusCode}");
            }

            var bytes = await response.Content.ReadAsByteArrayAsync(ct);
            if (bytes.Length == 0)
            {
                _logger.LogError("AIService /tts trả audio rỗng");
                throw new AiServiceException("AIService /tts trả audio rỗng");
            }

            var contentType = response.Content.Headers.ContentType?.MediaType ?? Mp3ContentType;
            return new QuestionSpeech(bytes, contentType);
        }
    }
}
