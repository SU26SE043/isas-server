namespace Isas.InterviewService.DTOs;

// TTS đọc câu hỏi — audio đã tổng hợp (hoặc lấy từ cache) để trả thẳng cho FE.
// Content = bytes mp3; ContentType = audio/mpeg (hợp đồng đã chốt với FE).
public record QuestionSpeech(byte[] Content, string ContentType);
