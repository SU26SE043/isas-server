namespace Isas.InterviewService.Services;

// BC7 — AIService `/analyze-cv` lỗi/không phản hồi hợp lệ → map 502 ở controller
// (phân biệt với InvalidOperationException = 400 "CV không đọc được").
public class AiServiceException : Exception
{
    public AiServiceException(string message) : base(message) { }
    public AiServiceException(string message, Exception inner) : base(message, inner) { }

    /// <summary>
    /// Lời gọi HẾT GIỜ (AIService không kịp trả lời) chứ không phải nó trả lỗi.
    /// </summary>
    /// <remarks>
    /// Hai ca này đòi hành động khác hẳn nhau: "AIService trả 5xx" là lỗi ở đó, còn "hết giờ"
    /// thường là CHẬM (nạp model lần đầu, hàng đợi dài) và tự khỏi ở lượt sau — đúng triệu chứng
    /// "câu đầu của buổi hỏng, các lần sau bình thường" mà người dùng báo 2026-08-15. Gộp cả hai
    /// vào 502 khiến người soi log không phân biệt được, mà client thì chỉ thấy "có lỗi".
    /// </remarks>
    public bool IsTimeout { get; init; }
}
