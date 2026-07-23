namespace Isas.CampaignService.Models
{
    /// <summary>F17 — cấu hình API key bên thứ ba (section <c>ApiKeys</c>).</summary>
    public class ApiKeySettings
    {
        public const string SectionName = "ApiKeys";

        /// <summary>Hạn mặc định khi tạo key mà caller không truyền <c>expiresInDays</c>.</summary>
        public int DefaultExpiryDays { get; set; } = 365;

        /// <summary>Trần hạn — caller không tự đặt key sống 100 năm.</summary>
        public int MaxExpiryDays { get; set; } = 730;

        /// <summary>Số key ACTIVE tối đa mỗi org (chặn org tự đẻ key vô hạn khó quản/khó revoke).</summary>
        public int MaxActiveKeysPerOrg { get; set; } = 10;

        /// <summary>
        /// Chỉ ghi <c>last_used_at</c> khi lần ghi trước đã cũ hơn ngần này phút — tránh 1 UPDATE
        /// mỗi request (contention + ghi khuếch đại) trong khi vẫn đủ tín hiệu "key còn ai dùng không".
        /// </summary>
        public int TouchThrottleMinutes { get; set; } = 15;

        /// <summary>Số request/cửa-sổ cho mỗi key (rate-limit). 0 = tắt.</summary>
        public int RateLimitPermitsPerWindow { get; set; } = 60;

        /// <summary>Độ dài cửa sổ rate-limit (giây).</summary>
        public int RateLimitWindowSeconds { get; set; } = 60;

        /// <summary>
        /// R2 — trần request/cửa-sổ cho bucket "anonymous" DÙNG CHUNG (request KHÔNG có api_key_id hợp lệ:
        /// thiếu header, key sai/hết hạn/thu hồi). CỐ Ý chặt hơn per-key (RateLimitPermitsPerWindow) — bucket
        /// này gộp MỌI request lạ vào 1 rổ, nên phải nhỏ để không ai dùng nó làm bàn đạp khoá key thật (đây
        /// chính là lỗi R2 gốc). Kẹp tối thiểu 1 ở nơi dùng, không để 0/âm tắt mất giới hạn ngoài ý muốn.
        /// </summary>
        public int AnonymousRateLimitPermitsPerWindow { get; set; } = 10;
    }
}
