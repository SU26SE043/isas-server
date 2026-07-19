using PaymentService.Models;

namespace Isas.PaymentService.DTOs
{
    /// <summary>
    /// F19 — một dòng biến động credit trả cho `GET /payment/me/credit-transactions`.
    ///
    /// Trước vòng này KHÔNG endpoint nào đọc <c>credit_transactions</c> cho bất kỳ ai, kể cả chủ ví:
    /// người dùng thấy được số dư (F7 <c>me/account</c>) nhưng không tra được vì sao nó thay đổi — mất
    /// 1 credit thì không có cách nào biết nó đi đâu.
    ///
    /// KHÔNG trả <c>owner_type</c>/<c>owner_id</c>: chủ ví suy từ JWT nên nhắc lại chính họ là thừa.
    /// </summary>
    public class CreditTransactionResponse
    {
        public Guid Id { get; set; }
        public int Delta { get; set; }
        public CreditTransactionReason Reason { get; set; }

        /// <summary>Đơn phát sinh (Purchase/Refund). NULL với Consume và các khoản được tặng.</summary>
        public Guid? OrderId { get; set; }

        /// <summary>Buổi phỏng vấn đã tiêu credit (Consume). Ref lỏng → InterviewService.</summary>
        public Guid? SessionId { get; set; }

        /// <summary>F18 — bút toán mua bị đảo (chỉ có trên dòng Refund).</summary>
        public Guid? ReversesTransactionId { get; set; }

        public DateTime CreatedAt { get; set; }

        public static CreditTransactionResponse ToResponse(CreditTransaction t) => new()
        {
            Id = t.Id,
            Delta = t.Delta,
            Reason = t.Reason,
            OrderId = t.OrderId,
            SessionId = t.SessionId,
            ReversesTransactionId = t.ReversesTransactionId,
            CreatedAt = t.CreatedAt,
        };
    }
}
