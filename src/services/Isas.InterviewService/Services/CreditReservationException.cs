namespace Isas.InterviewService.Services;

// BC2 — Payment /internal/credits/reserve trả 402 (ví hết credit / chạm hạn mức postpaid).
// Ném TRƯỚC khi tạo session row → controller map 402 (PAY-5: hết credit ⇒ KHÔNG có row session).
public class InsufficientCreditException : Exception
{
    public InsufficientCreditException(string message) : base(message) { }
}

// BC2 — PaymentService không phản hồi hợp lệ (down / timeout / 5xx / JSON hỏng).
// Phân biệt với InsufficientCreditException (402) → controller map 502 (như AiServiceException).
public class PaymentServiceException : Exception
{
    public PaymentServiceException(string message) : base(message) { }
    public PaymentServiceException(string message, Exception inner) : base(message, inner) { }
}
