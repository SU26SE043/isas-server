using System.ComponentModel.DataAnnotations;
using Isas.PaymentService.Services;

namespace Isas.PaymentService.DTOs
{
    /// <summary>F18 — thân request `POST /payment/admin/orders/{id}/refund`.</summary>
    public class RefundOrderRequest
    {
        /// <summary>Lý do hoàn (bắt buộc — hoàn tiền không có lý do thì không đối soát được).</summary>
        [Required]
        [StringLength(500, MinimumLength = 3)]
        public string Reason { get; set; } = null!;

        /// <summary>
        /// Mã giao dịch hoàn của cổng, admin nhập tay sau khi hoàn trên dashboard PayOS. Optional:
        /// hoàn ngoài luồng cổng (chuyển khoản tay) thì không có mã.
        /// </summary>
        [StringLength(100)]
        public string? GatewayRef { get; set; }

        /// <summary>
        /// Chấp nhận thu hồi ít hơn số credit đã bán (ví đã tiêu bớt). Mặc định <c>false</c> → hệ thống
        /// dừng lại và trả về số thu hồi được để admin quyết định, thay vì âm thầm để công ty chịu chênh.
        /// </summary>
        public bool AllowPartialClawback { get; set; }

        /// <summary>
        /// Admin khẳng định đã chuyển tiền thật cho khách ngay lúc hoàn (đã hoàn trên dashboard PayOS /
        /// chuyển khoản tay). Mặc định <c>false</c> = đơn vào trạng thái "chờ chuyển tiền", admin xác nhận
        /// sau qua <c>POST …/refund/settle</c> để không quên gửi tiền.
        /// </summary>
        public bool SettledNow { get; set; }
    }

    /// <summary>F18 — thân request `POST /payment/admin/orders/{id}/refund/settle` (xác nhận đã chuyển tiền).</summary>
    public class SettleRefundRequest
    {
        /// <summary>Mã giao dịch hoàn của PayOS (nếu có). Bỏ trống nếu chuyển khoản tay không mã.</summary>
        [StringLength(100)]
        public string? GatewayRef { get; set; }
    }

    /// <summary>F18 — phản hồi xác nhận đã chuyển tiền hoàn.</summary>
    public class SettleRefundResponse
    {
        public Guid OrderId { get; set; }
        public DateTime? RefundedAt { get; set; }
        public DateTime? RefundSettledAt { get; set; }
        public string? RefundGatewayRef { get; set; }

        public static SettleRefundResponse From(SettleRefundResult r) => new()
        {
            OrderId = r.OrderId,
            RefundedAt = r.RefundedAt,
            RefundSettledAt = r.RefundSettledAt,
            RefundGatewayRef = r.RefundGatewayRef,
        };
    }

    /// <summary>Phản hồi lệnh chi tiền hoàn tự động qua kênh chi payOS.</summary>
    public class RefundPayoutResponse
    {
        public Guid OrderId { get; set; }
        /// <summary>Mã lệnh chi phía payOS — dùng để đối soát khi cần lần theo dòng tiền.</summary>
        public string? PayoutId { get; set; }
        /// <summary>NULL = tiền chưa xác nhận tới khách (đang bay, hoặc cần người xử lý).</summary>
        public DateTime? RefundSettledAt { get; set; }
        public string Outcome { get; set; } = "";
        public string? Message { get; set; }

        public static RefundPayoutResponse From(RefundPayoutResult r) => new()
        {
            OrderId = r.OrderId,
            PayoutId = r.PayoutId,
            RefundSettledAt = r.RefundSettledAt,
            Outcome = r.Outcome.ToString(),
            Message = r.Message,
        };
    }

    /// <summary>
    /// F18 — phản hồi hoàn tiền. Trả CẢ số đã bán lẫn số thu hồi được: khi hai số lệch nhau thì phần
    /// chênh là khoản công ty mất, và người bấm nút cần thấy nó ngay tại chỗ.
    /// </summary>
    public class RefundOrderResponse
    {
        public Guid OrderId { get; set; }
        public long AmountVnd { get; set; }
        public int CreditsPurchased { get; set; }
        public int CreditsClawedBack { get; set; }
        public int ClawbackCeiling { get; set; }
        public Guid? RefundTransactionId { get; set; }
        public DateTime? RefundedAt { get; set; }
        /// <summary>NULL = đã hoàn nhưng CHƯA chuyển tiền cho khách (chờ kế toán).</summary>
        public DateTime? RefundSettledAt { get; set; }

        public static RefundOrderResponse From(RefundResult r) => new()
        {
            OrderId = r.OrderId,
            AmountVnd = r.AmountVnd,
            CreditsPurchased = r.CreditsPurchased,
            CreditsClawedBack = r.CreditsClawedBack,
            ClawbackCeiling = r.ClawbackCeiling,
            RefundTransactionId = r.RefundTransactionId,
            RefundedAt = r.RefundedAt,
            RefundSettledAt = r.RefundSettledAt,
        };
    }
}
