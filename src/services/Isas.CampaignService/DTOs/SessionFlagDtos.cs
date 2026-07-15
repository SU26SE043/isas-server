namespace Isas.CampaignService.DTOs
{
    /// <summary>
    /// SEC-1 ingest — cờ do FE/ứng viên phát (tab_switch/paste/focus_lost). candidateId + campaignId + sessionId
    /// lấy từ JWT + route; body chỉ mang loại tín hiệu + ghi chú.
    /// </summary>
    public class CandidateFlagRequest
    {
        public string SignalType { get; set; } = null!;
        public string? Note { get; set; }
    }

    /// <summary>
    /// SEC-1 ingest — cờ do AIService phát (face_mismatch/no_face/multiple_faces/multi_voice/identity_unverified).
    /// Gọi INTERNAL (X-Internal-Token, KHÔNG qua gateway — GEN-1) → mọi id trong body.
    /// </summary>
    public class InternalFlagRequest
    {
        public Guid SessionId { get; set; }
        public Guid CampaignId { get; set; }
        public Guid CandidateId { get; set; }
        public string SignalType { get; set; } = null!;
        public string? Note { get; set; }
    }
}
