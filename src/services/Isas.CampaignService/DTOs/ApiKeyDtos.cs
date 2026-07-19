using System.ComponentModel.DataAnnotations;

namespace Isas.CampaignService.DTOs
{
    // ── Quản lý key (JWT, OrgAdmin) ──────────────────────────────────────────

    /// <summary>F17 — tạo API key cho org.</summary>
    public class CreateApiKeyRequest
    {
        /// <summary>Nhãn để org phân biệt key ("Greenhouse production").</summary>
        [Required, StringLength(100, MinimumLength = 1)]
        public string Name { get; set; } = null!;

        /// <summary>Số ngày sống. Bỏ trống → <c>ApiKeys:DefaultExpiryDays</c>; vượt trần → 400.</summary>
        public int? ExpiresInDays { get; set; }

        /// <summary>
        /// Cho phép key đọc tên/email ứng viên. Mặc định false (deny-by-default) — bật là quyết định
        /// tường minh của OrgAdmin, không phải hệ quả phụ.
        /// </summary>
        public bool IncludePii { get; set; }
    }

    /// <summary>
    /// Trả về khi TẠO — trường <see cref="Key"/> là lần DUY NHẤT key thô tồn tại ngoài client.
    /// Không endpoint nào đọc lại được (DB chỉ có hash).
    /// </summary>
    public class CreatedApiKeyResponse
    {
        public Guid Id { get; set; }
        public string Name { get; set; } = null!;
        /// <summary>Key THÔ — hiện đúng một lần. Mất là phải tạo key mới.</summary>
        public string Key { get; set; } = null!;
        public string KeyPrefix { get; set; } = null!;
        public bool IncludePii { get; set; }
        public DateTime ExpiresAt { get; set; }
        public DateTime CreatedAt { get; set; }
    }

    /// <summary>Trả về khi LIỆT KÊ — không có key thô, không có hash.</summary>
    public class ApiKeyResponse
    {
        public Guid Id { get; set; }
        public string Name { get; set; } = null!;
        public string KeyPrefix { get; set; } = null!;
        public bool IncludePii { get; set; }
        public DateTime CreatedAt { get; set; }
        public DateTime ExpiresAt { get; set; }
        public DateTime? LastUsedAt { get; set; }
        public DateTime? RevokedAt { get; set; }
        /// <summary>Suy read-time: chưa revoke VÀ chưa quá hạn.</summary>
        public bool IsActive { get; set; }
    }

    // ── Public API (xác thực bằng key, KHÔNG phải JWT) ───────────────────────

    /// <summary>F17 — campaign ở dạng rút gọn cho ATS (đủ để phân trang/đối chiếu, không hơn).</summary>
    public class PublicCampaignSummary
    {
        public Guid Id { get; set; }
        public string Title { get; set; } = null!;
        public string Status { get; set; } = null!;
        public DateTime CreatedAt { get; set; }
        public DateTime? ExpiresAt { get; set; }
    }

    /// <summary>
    /// F17 — kết quả campaign cho bên thứ ba. **Hẹp có chủ đích**, KHÔNG phải
    /// <c>CampaignResultRow</c> nội bộ: đã bỏ <c>overrideNote</c> (ghi chú riêng của HR) và
    /// <c>flags[]</c> (tín hiệu chống gian lận kèm note — CAMP-12/D13 nói cờ là để HR đọc, đẩy sang
    /// ATS là mời auto-loại đúng thứ D13 cấm). Xem lý do đầy đủ trong docs/services/campaign.md §F17.
    /// </summary>
    public class PublicCampaignResultRow
    {
        public int Rank { get; set; }
        public Guid CandidateId { get; set; }
        public Guid SessionId { get; set; }

        /// <summary>Chỉ có khi key bật <c>includePii</c>; ngược lại null.</summary>
        public string? FullName { get; set; }
        /// <summary>Chỉ có khi key bật <c>includePii</c>; ngược lại null.</summary>
        public string? Email { get; set; }

        /// <summary>Điểm effective (đã áp HR override nếu có).</summary>
        public decimal TotalScore { get; set; }
        /// <summary>"Pass"/"Fail"; null khi org chưa đặt ngưỡng.</summary>
        public string? Result { get; set; }
        /// <summary>True = con người đã xem lại/chỉnh kết quả này (không lộ điểm AI gốc hay ghi chú).</summary>
        public bool HrReviewed { get; set; }
        public DateTime ScoredAt { get; set; }
    }

    public class PublicCampaignResultsResponse
    {
        public Guid CampaignId { get; set; }
        public decimal? PassScorePct { get; set; }
        public int TotalCandidates { get; set; }
        /// <summary>False khi key không bật <c>includePii</c> → fullName/email luôn null.</summary>
        public bool PiiIncluded { get; set; }
        public List<PublicCampaignResultRow> Results { get; set; } = new();
    }
}
