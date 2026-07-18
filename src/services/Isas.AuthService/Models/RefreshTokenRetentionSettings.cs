namespace Isas.AuthService.Models
{
    /// <summary>
    /// DB28 — cấu hình job dọn <c>refresh_tokens</c>. <c>Enabled=false</c> = tắt an toàn (không xoá gì).
    ///
    /// Vì sao cần: rotation chèn 1 row MỖI LẦN refresh và chỉ lật <c>is_revoked</c>, không bao giờ xoá.
    /// Access token TTL 15' ⇒ mỗi user hoạt động sinh hàng chục row/ngày; bảng phình vô hạn kèm UNIQUE
    /// btree trên <c>token</c> (hash 44 ký tự) + btree <c>user_id</c>. Đây là bảng nằm trên ĐƯỜNG LOGIN
    /// NÓNG, và <c>RevokeAllRefreshTokensAsync</c> (đăng xuất / đổi org-role) phải quét toàn bộ tập row
    /// của user đó mỗi lần gọi → càng dùng lâu càng chậm đúng chỗ đau nhất.
    /// </summary>
    public class RefreshTokenRetentionSettings
    {
        public const string SectionName = "RefreshTokenRetention";

        /// <summary>Số ngày giữ mặc định khi cấu hình trống/không hợp lệ.</summary>
        public const int DefaultRetentionDays = 30;

        /// <summary>
        /// Sàn cứng cho ngưỡng giữ. Cấu hình nhỏ hơn bị nâng lên đây — chặn cấu hình sai tay
        /// (vd đặt 0) biến job dọn rác thành job xoá token đang sống.
        /// </summary>
        public const int MinRetentionDays = 1;

        public bool Enabled { get; set; } = true;

        /// <summary>
        /// Chỉ xoá row đã chết lâu hơn ngần này ngày. Mặc định 30 — rộng gấp bội mọi cửa sổ còn dùng
        /// được: refresh token sống 7 ngày (<c>Jwt:RefreshTokenDays</c>) và cửa sổ ân hạn AUTH-9 tính
        /// bằng GIÂY (mặc định 60s). Ngưỡng thực tế còn được nâng theo cửa sổ ân hạn — xem
        /// <c>RefreshTokenPurge.ResolveRetentionDays</c>.
        /// </summary>
        public int RetentionDays { get; set; } = DefaultRetentionDays;

        /// <summary>Chu kỳ quét. Rác này tích chậm (theo ngày) nên không cần quét dày.</summary>
        public int ScanIntervalMinutes { get; set; } = 60;

        /// <summary>Số row xoá mỗi lệnh DELETE — chia nhỏ để không giữ lock dài trên bảng login nóng.</summary>
        public int BatchSize { get; set; } = 5000;

        /// <summary>
        /// Trần số batch mỗi vòng quét. Lần chạy ĐẦU trên DB đã tích lũy lâu có thể có hàng triệu row —
        /// trần này giữ mỗi vòng ngắn, phần còn lại để vòng sau (dọn dần, không khoá bảng hàng phút).
        /// </summary>
        public int MaxBatchesPerRun { get; set; } = 20;

        public int EffectiveBatchSize => BatchSize > 0 ? BatchSize : 5000;
        public int EffectiveMaxBatchesPerRun => MaxBatchesPerRun > 0 ? MaxBatchesPerRun : 20;
    }
}
