namespace Isas.CampaignService.Models
{
    /// <summary>
    /// E11c — LỊCH SỬ điều chỉnh kết quả của HR (append-only), một dòng cho MỖI lần HR đặt/huỷ override.
    ///
    /// Vì sao tách bảng: 5 cột <c>override_*</c> trên <c>campaign_rankings</c> chỉ giữ trạng thái HIỆN TẠI —
    /// lần override sau ghi đè lần trước, huỷ thì set hết về null ⇒ HR mất "ai từng sửa, lý do gì". Trail duy nhất
    /// trước đó là <c>audit_logs.summary</c> (chuỗi tự do, không endpoint nào cho HR đọc). Đây là mục P2
    /// "tách <c>ranking_overrides</c> ra bảng phụ" (tasks.md), làm khi chạm feature.
    ///
    /// Ranking row vẫn là NGUỒN cho ranking sort / CSV / preview (đọc <c>override_*</c>); bảng này là THÊM,
    /// không thay. Hai thứ được ghi trong CÙNG một SaveChanges với audit (nguyên tử).
    /// </summary>
    public class RankingOverride
    {
        public Guid Id { get; set; }
        public Guid RankingId { get; set; }     // FK → campaign_rankings (Restrict; ranking không bao giờ bị xoá vật lý)
        public Guid CampaignId { get; set; }    // denorm để lọc/backfill không cần join
        public Guid SessionId { get; set; }     // ref lỏng → Interview (GEN-2)

        /// <summary>"Set" = HR đặt điểm/kết quả · "Clear" = HR huỷ về điểm AI. Lưu string (GEN-2), KHÔNG CHECK —
        /// thêm giá trị mới không được thành cửa một chiều ở Down() (bài học ck_audit_logs_action).</summary>
        public string Kind { get; set; } = null!;
        public decimal? Score { get; set; }     // null khi Clear, hoặc HR chỉ đổi kết quả
        public string? Result { get; set; }     // "Pass" | "Fail" | null
        public string Note { get; set; } = null!;   // lý do — bắt buộc ở cả Set lẫn Clear (service đã ép)

        public Guid ActorUserId { get; set; }   // user sub HR thao tác
        /// <summary>Snapshot claim <c>email</c> của JWT lúc ghi (GEN-3: không gọi Auth lúc chạy, Campaign không có
        /// bảng user). NULL = KHÔNG BIẾT (dòng backfill từ audit_logs) — FE hiện "không rõ", KHÔNG đoán (BK23).</summary>
        public string? ActorEmail { get; set; }

        /// <summary>"Live" = ghi lúc HR bấm · "AuditBackfill" = dựng lại từ <c>audit_logs</c> bằng migration.</summary>
        public string Source { get; set; } = null!;
        public DateTime CreatedAt { get; set; } // = OverriddenAt của ranking (cùng một `now`)

        public CampaignRanking Ranking { get; set; } = null!;
    }
}
