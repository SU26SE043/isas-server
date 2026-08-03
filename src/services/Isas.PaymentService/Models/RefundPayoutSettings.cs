namespace PaymentService.Models
{
    /// <summary>
    /// Cấu hình hoàn tiền tự động qua kênh CHI payOS (<c>/v1/payouts</c>). Bind section
    /// <c>RefundPayout</c>. Config thuần → KHÔNG migration.
    ///
    /// <para><b>Credential nằm ở section riêng <c>PayOS:Payout</c>, không dùng chung với kênh thu.</b>
    /// Đã kiểm chứng bằng lệnh gọi thật: dùng <c>PayOS:ApiKey</c> (kênh thu) gọi API chi trả
    /// <c>code 601 "API key không tồn tại"</c>. Docs payOS cũng ghi checksum key của kênh chuyển tiền
    /// được sinh khi TẠO KÊNH — tức là một bộ credential khác hẳn.</para>
    ///
    /// <para><b>Mặc định TẮT.</b> Đây là đường duy nhất trong service tự chuyển tiền RA khỏi tài khoản
    /// công ty, nên nó phải được bật tường minh sau khi kênh chi đã được đối soát — không phải bật
    /// theo mặc định vì lỡ deploy (tiền lệ <c>Tiering:Enabled</c>, <c>InvoiceOverdue:Enabled</c>).</para>
    /// </summary>
    public class RefundPayoutSettings
    {
        public const string SectionName = "RefundPayout";

        /// <summary>Tắt hẳn cả nút bấm lẫn reconciler (safe-disable). Mặc định TẮT — xem phần tóm tắt.</summary>
        public bool Enabled { get; set; }

        public int ScanIntervalSeconds { get; set; } = 120;
        public int BatchSize { get; set; } = 100;

        /// <summary>
        /// Trần số tiền cho MỘT lệnh chi tự động. Vượt trần → từ chối, admin chuyển tay.
        ///
        /// Đây là phanh tay cho trường hợp logic hoàn tiền sai và hệ thống bắt đầu bắn lệnh hàng loạt:
        /// giới hạn thiệt hại mỗi lệnh về một con số nhìn thấy được. <c>0</c> = chặn mọi lệnh tự động
        /// (kill-switch phụ, độc lập với <see cref="Enabled"/>).
        /// </summary>
        public long MaxAutoPayoutVnd { get; set; } = 2_000_000;

        /// <summary>
        /// Tuổi tối thiểu của lệnh chi đang bay trước khi reconciler hỏi lại trạng thái. Hỏi ngay lập tức
        /// thì gần như chắc chắn nhận về <c>Processing</c> (ngân hàng chưa xử xong) — chỉ tốn request.
        /// </summary>
        public int PollAfterSeconds { get; set; } = 60;

        /// <summary>
        /// Ánh xạ <c>counterAccountBankId</c> (webhook thu) → <c>toBin</c> (lệnh chi), do ops điền bằng
        /// config sau khi payOS xác nhận hệ mã. Rỗng = chỉ những mã vốn đã là BIN mới chi tự động được,
        /// phần còn lại rơi về chuyển tay — xem <see cref="Isas.PaymentService.Services.BankBinResolver"/>.
        ///
        /// Để ở config chứ không hardcode vì đây là dữ liệu tra cứu của bên thứ ba: nó thay đổi khi có
        /// ngân hàng mới, và sửa nó không nên phải build lại image.
        /// </summary>
        public Dictionary<string, string> BankBinMap { get; set; } = new();
    }

    /// <summary>
    /// Credential kênh CHI payOS — bind section <c>PayOS:Payout</c>. Tách khỏi
    /// <see cref="Isas.PaymentService.Models.PayOSSettings"/> vì đây là bộ khoá của một kênh KHÁC
    /// (xem <see cref="RefundPayoutSettings"/>). Trống = chưa cấu hình → không dựng client chi.
    /// </summary>
    public class PayoutChannelSettings
    {
        public const string SectionName = "PayOS:Payout";

        public string ClientId { get; set; } = "";
        public string ApiKey { get; set; } = "";
        public string ChecksumKey { get; set; } = "";

        public bool IsConfigured =>
            !string.IsNullOrWhiteSpace(ClientId)
            && !string.IsNullOrWhiteSpace(ApiKey)
            && !string.IsNullOrWhiteSpace(ChecksumKey);
    }
}
