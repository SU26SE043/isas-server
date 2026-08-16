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

        // Đo thời gian để log phân biệt được "hết giờ" với "lỗi ngay lập tức". Không có con số này
        // thì giả thuyết cold-start (nạp giọng lần đầu) không kiểm chứng được từ log — mà đó đúng
        // là triệu chứng đã báo: câu ĐẦU của buổi hỏng, các lần sau bình thường.
        var started = System.Diagnostics.Stopwatch.StartNew();
        HttpResponseMessage response;
        try
        {
            response = await _httpClient.SendAsync(msg, ct);
        }
        catch (TaskCanceledException ex) when (!ct.IsCancellationRequested)
        {
            // `ct` chưa bị huỷ ⇒ HttpClient tự huỷ vì HẾT GIỜ, không phải người dùng bỏ trang.
            _logger.LogError(ex,
                "AIService /tts HẾT GIỜ sau {Elapsed}ms (timeout client {Timeout}s)",
                started.ElapsedMilliseconds, _httpClient.Timeout.TotalSeconds);
            throw new AiServiceException("AIService /tts hết giờ", ex) { IsTimeout = true };
        }
        catch (TaskCanceledException ex)
        {
            // Người dùng rời trang/huỷ request — KHÔNG phải lỗi của TTS, đừng đếm vào lỗi vendor.
            _logger.LogInformation(ex,
                "Người gọi huỷ request /tts sau {Elapsed}ms", started.ElapsedMilliseconds);
            throw new AiServiceException("Request /tts bị huỷ", ex);
        }
        catch (HttpRequestException ex)
        {
            _logger.LogError(ex,
                "Không nối được AIService /tts sau {Elapsed}ms (mạng/DNS/cổng — KHÔNG phải lỗi vendor TTS)",
                started.ElapsedMilliseconds);
            throw new AiServiceException("Không gọi được AIService /tts", ex);
        }

        using (response)
        {
            if (!response.IsSuccessStatusCode)
            {
                var error = await response.Content.ReadAsStringAsync(ct);
                // Phân biệt rõ trong log: đây là AIService TRẢ LỜI bằng mã lỗi (có thân lỗi để đọc),
                // khác hẳn hai nhánh hết-giờ/không-nối-được ở trên.
                _logger.LogError(
                    "AIService /tts TRẢ LỖI {StatusCode} sau {Elapsed}ms - {Error}",
                    (int)response.StatusCode, started.ElapsedMilliseconds, error);
                throw new AiServiceException($"AIService /tts trả {(int)response.StatusCode}");
            }

            var bytes = await response.Content.ReadAsByteArrayAsync(ct);
            if (bytes.Length == 0)
            {
                _logger.LogError("AIService /tts trả 200 nhưng audio RỖNG (sau {Elapsed}ms)",
                    started.ElapsedMilliseconds);
                throw new AiServiceException("AIService /tts trả audio rỗng");
            }

            var contentType = response.Content.Headers.ContentType?.MediaType ?? Mp3ContentType;
            return new QuestionSpeech(bytes, contentType);
        }
    }
}
