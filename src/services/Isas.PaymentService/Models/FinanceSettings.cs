namespace Isas.PaymentService.Models
{
    /// <summary>Tỷ giá quy đổi USD→VND dùng cho báo cáo tài chính (F19 gross margin). KHÔNG phải tỷ giá
    /// real-time — admin tự cập nhật khi lệch nhiều. Đơn giá AI (<c>ai_usage_logs.cost_usd</c>) đã snapshot
    /// USD tại thời điểm gọi; quy đổi VND chỉ xảy ra LÚC ĐỌC báo cáo, không ghi ngược vào bảng gốc.</summary>
    public class FinanceSettings
    {
        public decimal UsdToVndRate { get; set; } = 26000m;
    }
}
