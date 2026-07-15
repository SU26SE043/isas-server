namespace Isas.CampaignService.Models
{
    /// <summary>
    /// Cờ chống gian lận cho HR (D13/CAMP-12 — FLAG cho HR, KHÔNG auto-hủy). 1 dòng = 1 tín hiệu phát hiện.
    /// Backend CHỈ NHẬN + LƯU + PHƠI cờ; KHÔNG tự phát hiện gian lận. Nguồn phát:
    ///  - Frontend (webcam/tab-switch, repo riêng): tab_switch · paste · focus_lost.
    ///  - AIService (face-match / multi-voice, service riêng): face_mismatch · no_face · multiple_faces ·
    ///    multi_voice · identity_unverified.
    /// Chỉ B2B (campaign org-owned). Ref lỏng (Guid, GEN-2) — KHÔNG FK xuyên service tới session/candidate.
    /// </summary>
    public class SessionFlag
    {
        public Guid Id { get; set; }
        public Guid SessionId { get; set; }      // ref lỏng → Interview; index (gom cờ/1 buổi)
        public Guid CampaignId { get; set; }     // gate theo campaign.anti_cheat_enabled / face_verify_enabled
        public Guid CandidateId { get; set; }    // ref lỏng → ứng viên bị flag
        public string SignalType { get; set; } = null!;   // loại tín hiệu (whitelist ở controller)
        public string? Note { get; set; }        // chi tiết cho HR (vd "2 khuôn mặt trong khung")
        public DateTime DetectedAt { get; set; }  // thời điểm phát hiện (server nhận)
    }
}
