using Isas.Shared.Scoring;

namespace Isas.CampaignService.Models
{
    /// <summary>
    /// Ranking read-model B2B (E4/D10) — cập nhật bằng event <c>SessionScored</c> (RabbitMQ),
    /// KHÔNG gọi HTTP đọc điểm mỗi lần xem dashboard (campaign.md §campaign_rankings).
    /// Idempotent: UNIQUE(session_id) — event tới 2 lần (redelivery/duplicate) vẫn chỉ 1 row (upsert).
    /// Rank + Pass/Fail do E5 (<c>GetCampaignResultsAsync</c>) tính READ-TIME từ <c>TotalScore</c> —
    /// KHÔNG lưu thành cột (BK1: đã drop cột chết <c>rank</c>/<c>result</c> mà E4 từng tạo nhưng E5 không đọc).
    /// E4 chỉ ghi <c>TotalScore</c> (điểm có trọng số Interview đã tính sẵn).
    /// </summary>
    public class CampaignRanking
    {
        public Guid Id { get; set; }
        public Guid CampaignId { get; set; }
        public Guid CandidateId { get; set; }
        public Guid SessionId { get; set; }   // ref lỏng → Interview; UNIQUE (upsert idempotent)
        public decimal TotalScore { get; set; }
        public DateTime UpdatedAt { get; set; }

        // CAMP-18 — thước đo đã chấm buổi này. NULL = KHÔNG BIẾT (buổi chấm trước khi có nhãn), và
        // ⚠ null KHÔNG được suy thành v1: suy "biết" từ "không biết" đúng là lỗi BK23 sinh ra để chặn.
        // Cần nhãn vì đổi mốc là đổi thước đo mạnh hơn cả thu hẹp phạm vi chấm — mà CAMP-10 (xếp hạng),
        // BC15 (đo cải thiện) và F14 (mốc peer) đang đem điểm so THẲNG với nhau.
        public int? RubricVersion { get; set; }

        // SCP1 · B5 — BÓ BIẾN ĐẦU VÀO THÔ của lượt chấm này, đến QUA event SessionScored, ghi lúc
        // upsert ranking. Lưu RAW per-criterion ({name,pct,weight,maxScore} + answered/totalQuestions),
        // KHÔNG lưu scalar đã tính — B8 (xem trước / áp chính sách) dựng lại ScoringContext từ đây và
        // chạy biểu thức, kể cả cho hàng lịch sử khi HĐ-1 thêm biến mới.
        //
        // ⚠ NULLABLE bắt buộc (CẤM #4): field đến qua event ⇒ bản Interview cũ / event cũ trong outbox
        // không mang nó ⇒ NOT NULL sẽ crash consumer trong cửa sổ rollout. jsonb (Npgsql) / text (SQLite).
        public ScoringInputsSnapshot? ScoringInputs { get; set; }

        // E11b — HR chốt điểm cuối (điểm AI = gợi ý). Null = chưa override → dùng TotalScore/ngưỡng.
        // Điểm/kết-quả effective read-time = OverrideScore ?? TotalScore, OverrideResult ?? (theo ngưỡng).
        // TotalScore giữ nguyên snapshot AI (E4 redelivery không đè override).
        public decimal? OverrideScore { get; set; }
        public string? OverrideResult { get; set; }   // "Pass" | "Fail"
        public string? OverrideNote { get; set; }
        public Guid? OverriddenBy { get; set; }        // user sub HR thao tác
        public DateTime? OverriddenAt { get; set; }

        // Navigation (DB9) — FK nội-service campaign_rankings.campaign_id → campaigns.id (Restrict).
        // Required nav (CampaignId NOT NULL) → cần query filter khớp soft-delete Campaign (xem DbContext).
        // Ref XUYÊN service CandidateId/SessionId → giữ Guid lỏng (GEN-2), KHÔNG FK.
        public Campaign Campaign { get; set; } = null!;
    }
}
