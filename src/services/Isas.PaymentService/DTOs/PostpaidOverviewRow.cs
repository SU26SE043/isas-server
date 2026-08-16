namespace Isas.PaymentService.DTOs;

/// <summary>
/// Bậc cảnh báo postpaid cho worklist admin — thang leo dần theo mức khẩn, đủ để admin thấy vấn đề
/// TRƯỚC khi một buổi phỏng vấn thật bị 402 giữa chừng (BK17/F23). Giá trị SỐ tăng dần theo mức khẩn
/// để sắp xếp được trực tiếp bằng so sánh số (Overdue &gt; DueSoon &gt; InvoiceIssued &gt; ApproachingLimit &gt; None).
/// </summary>
public enum PostpaidAlertLevel
{
    /// <summary>Không có gì đáng báo — usage dưới ngưỡng, không hoá đơn nào đang chờ.</summary>
    None = 0,

    /// <summary>Usage + reserved đã chạm <see cref="Isas.PaymentService.Models.BillingSettings.ApproachingLimitRatio"/>
    /// của hạn mức, nhưng CHƯA có hoá đơn nào chờ trả — cảnh báo SỚM NHẤT, trước khi kỳ chốt.</summary>
    ApproachingLimit = 1,

    /// <summary>Vừa chốt kỳ, có hoá đơn Issued, còn nhiều ngày mới tới hạn (chưa vào cửa sổ DueSoon).</summary>
    InvoiceIssued = 2,

    /// <summary>Có hoá đơn Issued với DueAt trong vòng <see cref="Isas.PaymentService.Models.BillingSettings.DueSoonDays"/>
    /// ngày tới — sắp bị chặn nếu không trả kịp.</summary>
    DueSoon = 3,

    /// <summary>Có hoá đơn Overdue — BK17 ĐANG CHẶN reserve mới cho org này.</summary>
    Overdue = 4
}

/// <summary>
/// Thông tin tổng quan về tài khoản postpaid.
/// </summary>
public sealed record PostpaidOverviewRow(
    /// <summary>
    /// ID chủ sở hữu tài khoản.
    /// </summary>
    Guid OwnerId,

    /// <summary>
    /// Hạn mức tín dụng cho tài khoản. Có thể là null nếu chưa đặt hạn mức.
    /// </summary>
    int? CreditLimit,

    /// <summary>
    /// Số lượng lượt sử dụng trong kỳ hiện tại.
    /// </summary>
    int PeriodUsage,

    /// <summary>
    /// Số lượng lượt đã được giữ chỗ.
    /// </summary>
    int ReservedCredits,

    /// <summary>
    /// Hạn mức còn lại trước khi đạt đến hạn mức tín dụng. Là null nếu chưa đặt hạn mức.
    /// </summary>
    int? Headroom,

    /// <summary>
    /// Số tiền cần phải trả trong kỳ hiện tại nếu chốt ngay bây giờ.
    /// </summary>
    decimal PendingAmountVnd,

    /// <summary>
    /// Số lượng hóa đơn chưa được thanh toán.
    /// </summary>
    int UnpaidInvoiceCount,

    /// <summary>
    /// Xác định liệu tổ chức có bị chặn đặt chỗ mới vì còn hóa đơn quá hạn hay không.
    /// </summary>
    bool HasOverdue,

    /// <summary>
    /// Mốc kết thúc kỳ của hóa đơn gần nhất. Là null nếu chưa từng chốt kỳ nào.
    /// </summary>
    DateTime? LastInvoicePeriodEnd,

    /// <summary>
    /// Bậc cảnh báo hiện tại (đã tính sẵn, mức khẩn nhất thắng). Worklist sắp theo trường này TRƯỚC
    /// tiên. Mặc định <see cref="PostpaidAlertLevel.None"/> khi không truyền (tương thích ngược).
    /// </summary>
    PostpaidAlertLevel AlertLevel = PostpaidAlertLevel.None
);
