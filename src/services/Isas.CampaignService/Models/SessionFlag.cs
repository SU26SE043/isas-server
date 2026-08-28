namespace Isas.CampaignService.Models
{
    /// <summary>
    /// Cờ chống gian lận cho HR (D13/CAMP-12 — FLAG cho HR, KHÔNG auto-hủy). 1 dòng = 1 tín hiệu phát hiện.
    /// Backend CHỈ NHẬN + LƯU + PHƠI cờ; KHÔNG tự phát hiện gian lận. Nguồn phát:
    ///  - Frontend (webcam/tab-switch, repo riêng): tab_switch · paste · focus_lost · camera_blocked ·
    ///    monitoring_gap (hai cờ sau là MÔI TRƯỜNG — "không quan sát được", không phải "sai người").
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

        // MON1-B1 — ai ghi cờ này. Enum lưu STRING (GEN-2). Mặc định Client: mọi cờ hôm nay đều đi qua
        // createCampaignFlag từ trình duyệt ứng viên (kể cả no_face — ảnh cũng do client chụp rồi gửi),
        // nên ứng viên CHẶN được. Server = cờ server tự suy từ face_images (B2/B3: captured_at ngừng
        // tiến trong khi buổi vẫn chạy) — nằm trên dòng client không can thiệp được.
        public FlagSource Source { get; set; } = FlagSource.Client;

        // Navigation (DB9) — FK nội-service session_flags.campaign_id → campaigns.id (Restrict).
        // Required nav (CampaignId NOT NULL) → cần query filter khớp soft-delete Campaign (xem DbContext).
        // Ref XUYÊN service SessionId/CandidateId → giữ Guid lỏng (GEN-2), KHÔNG FK.
        public Campaign Campaign { get; set; } = null!;
    }

    /// <summary>
    /// Nguồn ghi <see cref="SessionFlag"/> (lưu string — GEN-2). <c>Client = 0</c> có chủ đích:
    /// giá trị zero của enum = mặc định an toàn (mọi cờ chưa gán rõ nguồn = do client báo).
    /// </summary>
    public enum FlagSource
    {
        /// <summary>Cờ do trình duyệt ứng viên tự báo (webcam/tab-switch/paste…). Ứng viên CHẶN được endpoint.</summary>
        Client = 0,
        /// <summary>Cờ do server tự suy ra từ face_images (nhịp captured_at). Ứng viên KHÔNG chặn được.</summary>
        Server = 1
    }
}
