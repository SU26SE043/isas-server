namespace Isas.CampaignService.Models
{
    /// <summary>
    /// F17 (FR14) — API key cấp cho **ORG** để bên thứ ba (ATS: Greenhouse/Lever/Workday…) đọc
    /// kết quả campaign qua Public API, KHÔNG cần phiên người dùng.
    ///
    /// Chủ sở hữu = ORG chứ không phải cá nhân HR (AUTH-8: billing/campaign gắn theo org). Hệ quả
    /// có chủ đích: HR nghỉ việc → key vẫn chạy (tích hợp ATS không gãy theo nhân sự) — đánh đổi là
    /// phải revoke tay, xem §Rủi ro còn lại trong docs/services/campaign.md.
    ///
    /// KHÔNG lưu key thô — chỉ <see cref="KeyHash"/>. Cùng lược đồ SHA-256 với refresh token (DB12)
    /// và invitation token (DB23): đọc được DB/backup ≠ gọi được API thay org.
    /// </summary>
    public class ApiKey
    {
        public Guid Id { get; set; }

        /// <summary>ORG sở hữu key — MỌI truy vấn qua key này bị kẹp vào đúng org này (AUTH-8).</summary>
        public Guid OrgId { get; set; }

        /// <summary>Nhãn người đọc được ("Greenhouse production") — để org biết revoke cái nào.</summary>
        public string Name { get; set; } = null!;

        /// <summary>
        /// SHA-256(UTF8(key thô)) base64 (44 ký tự). UNIQUE → tra cứu = single-row index probe.
        /// </summary>
        public string KeyHash { get; set; } = null!;

        /// <summary>
        /// 6 ký tự đầu phần ngẫu nhiên, để hiện trong danh sách ("isas_ak_3f9c2a…") cho org phân biệt
        /// key mà KHÔNG cần lộ key thô. An toàn: lộ 6/43 ký tự base64url vẫn còn ~220 bit entropy
        /// (chuẩn ngành — GitHub/Stripe đều hiện tiền tố). Không dùng để tra cứu/xác thực.
        /// </summary>
        public string KeyPrefix { get; set; } = null!;

        /// <summary>
        /// Cho phép trả **tên/email ứng viên** (PII, F5) qua Public API. Mặc định **false** —
        /// deny-by-default: tích hợp chỉ cần điểm thì không được cầm PII. Bật tường minh lúc tạo key.
        /// </summary>
        public bool IncludePii { get; set; }

        /// <summary>Cá nhân (user sub) đã tạo key — giữ danh tính người cho audit (AUTH-8).</summary>
        public Guid CreatedByUserId { get; set; }

        public DateTime CreatedAt { get; set; }

        /// <summary>
        /// LUÔN có hạn (NOT NULL) — bài học DB23: cột hạn nullable ⇒ credential sống vĩnh viễn.
        /// Mặc định <c>ApiKeys:DefaultExpiryDays</c> khi caller không truyền.
        /// </summary>
        public DateTime ExpiresAt { get; set; }

        /// <summary>
        /// Lần gọi gần nhất (ghi có tiết chế — xem <c>ApiKeys:TouchThrottleMinutes</c>). Không có
        /// tín hiệu này thì org không dám revoke key nào vì không biết cái nào còn ai dùng.
        /// </summary>
        public DateTime? LastUsedAt { get; set; }

        /// <summary>Thu hồi = soft (giữ row cho audit); set → mọi request bằng key này 401.</summary>
        public DateTime? RevokedAt { get; set; }
    }
}
