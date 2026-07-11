namespace Isas.InterviewService.Services;

// BC7 — AIService `/analyze-cv` lỗi/không phản hồi hợp lệ → map 502 ở controller
// (phân biệt với InvalidOperationException = 400 "CV không đọc được").
public class AiServiceException : Exception
{
    public AiServiceException(string message) : base(message) { }
    public AiServiceException(string message, Exception inner) : base(message, inner) { }
}
