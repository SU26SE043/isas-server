using Isas.InterviewService.DTOs;

namespace Isas.InterviewService.Services.Interfaces;

// TTS đọc câu hỏi — typed HttpClient gọi AIService `/tts` (máy-máy, X-Internal-Token, KHÔNG qua
// gateway). AIService giữ TOÀN BỘ phần vendor: gọi Gemini TTS + cache mp3 trên S3 theo nội dung.
// Interview chỉ kiểm quyền rồi chuyển tiếp bytes — không biết vendor nào, không giữ key cache.
public interface IAiServiceSpeechSynthesizer
{
    Task<QuestionSpeech> SynthesizeAsync(string text, CancellationToken ct = default);
}
