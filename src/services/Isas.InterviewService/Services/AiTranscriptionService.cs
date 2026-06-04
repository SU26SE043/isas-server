// namespace Isas.InterviewService.Services;
//
// // Services/AiTranscriptionService.cs
// using System.Net.Http.Headers;
// using Isas.InterviewService.Services;
//
//
// public class AiTranscriptionService(
//     HttpClient http,
//     IFileService fileService) : ITranscriptionService
// {
//     public async Task<string> TranscribeAsync(Guid audioFileId, CancellationToken ct = default)
//     {
//         // 1. Lấy file record + tải bytes từ SeaweedFS
//         var file = await fileService.GetAsync(audioFileId, ct)
//                    ?? throw new KeyNotFoundException("Không tìm thấy file audio.");
//
//         // GIẢ ĐỊNH IFileService có cách lấy stream — xem ghi chú bên dưới
//         await using var audioStream = await fileService.OpenReadAsync(audioFileId, ct);
//
//         // 2. Gửi multipart sang AIService /transcribe
//         using var content = new MultipartFormDataContent();
//         var streamContent = new StreamContent(audioStream);
//         streamContent.Headers.ContentType =
//             new MediaTypeHeaderValue(file.MimeType ?? "audio/wav");
//         content.Add(streamContent, "file", file.OriginalName ?? "audio.wav");
//         content.Add(new StringContent("vi"), "language");
//
//         var response = await http.PostAsync("/transcribe", content, ct);
//         response.EnsureSuccessStatusCode();
//
//         var result = await response.Content.ReadFromJsonAsync<TranscribeResult>(ct)
//                      ?? throw new InvalidOperationException("AIService trả về rỗng.");
//
//         return result.Text;
//     }
//
//     private sealed record TranscribeResult(string Text);
// }