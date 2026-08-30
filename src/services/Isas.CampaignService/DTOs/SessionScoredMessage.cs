using Isas.Shared.Scoring;

namespace Isas.CampaignService.DTOs
{
    /// <summary>
    /// Shape của event <c>SessionScored</c> nhận từ InterviewService qua RabbitMQ
    /// (exchange <c>interview.events</c> topic, routing key <c>session.scored</c> —
    /// interview.md §Sự kiện phát ra). Bản sao CỤC BỘ trong CampaignService — KHÔNG
    /// tham chiếu thẳng code/DLL InterviewService (GEN-2: không FK/dependency xuyên
    /// service, ref lỏng bằng Guid). Field khớp
    /// <c>Isas.InterviewService.DTOs.SessionScoredEvent</c>.
    /// </summary>
    public class SessionScoredMessage
    {
        public Guid SessionId { get; set; }

        // null = B2C → E4 chỉ xếp hạng B2B, bỏ qua (không tạo row campaign_rankings).
        public Guid? CampaignId { get; set; }

        public Guid CandidateId { get; set; }

        // Điểm tổng ĐÃ có trọng số (Σ điểm_tiêu_chí × weight, kẹp [0,100]) — Interview tính sẵn,
        // Campaign lưu nguyên, KHÔNG recompute.
        public decimal TotalScore { get; set; }

        public DateTime ScoredAt { get; set; }

        // CAMP-18 — bản thước đo Interview đã dùng để chấm buổi này. NULLABLE có chủ đích: bản
        // Interview cũ không gửi field này, và hai service deploy không nguyên tử ⇒ thiếu thì để
        // NULL ("không biết"), tuyệt đối không mặc định thành 1.
        public int? RubricVersion { get; set; }

        // SCP1 · B5 — BÓ BIẾN ĐẦU VÀO THÔ (per-criterion pct/weight/maxScore/name + answered/
        // totalQuestions). Ghim vào campaign_rankings.scoring_inputs lúc upsert. NULLABLE: bản
        // Interview cũ / event cũ trong outbox không mang field này ⇒ để null, KHÔNG crash consumer.
        public ScoringInputsSnapshot? ScoringInputs { get; set; }

        // SCP1 · B6 / HĐ-5 — CỜ LÙI AN TOÀN: true = biểu thức chính sách LỖI lúc chạy trên buổi này ⇒
        // TotalScore được tính bằng công thức weighted mặc định. Campaign ghi vào
        // campaign_rankings.score_fallback → bảng kết quả + CSV hiện được "đây là điểm mặc định".
        //
        // ⚠ B10 — TRƯỚC bản này lớp SessionScoredMessage KHÔNG khai property này, mà Interview VẪN
        // phát nó (SessionScoredEvent.cs:49) ⇒ System.Text.Json bỏ qua khoá lạ ⇒ cờ MẤT, không lỗi
        // không log. bool (mặc định false): event cũ / bản Interview cũ không gửi ⇒ false = "không
        // lùi an toàn", đúng nghĩa an toàn.
        public bool ScoreFallback { get; set; }

        // SCP1 · B10 / HĐ-5 — phiên bản chính sách chấm ĐÃ GHIM trên buổi (đến QUA event; Interview
        // đọc thẳng từ practice_sessions.campaign_policy_version). Campaign ghi vào
        // campaign_rankings.policy_version + tra tên (scoring_policies theo campaign_id + Kind +
        // version NÀY) ghi policy_name ⇒ bảng kết quả gắn nhãn "điểm do chính sách v{N}" NGAY trên
        // đường chấm thường, không đợi HR bấm "áp" (B8).
        //
        // ⚠ NULLABLE bắt buộc: field đến qua event ⇒ bản Interview cũ / event cũ trong outbox không
        // mang nó ⇒ null = "buổi không ghim chính sách", KHÔNG suy thành v1 (BK23).
        public int? CampaignPolicyVersion { get; set; }
    }
}
