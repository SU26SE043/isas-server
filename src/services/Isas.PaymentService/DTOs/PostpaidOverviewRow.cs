namespace Isas.PaymentService.DTOs;

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
    DateTime? LastInvoicePeriodEnd
);
