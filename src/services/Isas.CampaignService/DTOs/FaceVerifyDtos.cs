namespace Isas.CampaignService.DTOs
{
    /// <summary>
    /// SEC-2 — kết quả 1 lần giám sát khuôn mặt (face-check) trả cho FE. D13: chỉ báo, KHÔNG chặn bài.
    /// Mỗi tín hiệu trong <c>Signals</c> đã được ghi thành 1 cờ session_flags cho HR.
    /// </summary>
    public class FaceCheckResponse
    {
        public bool Match { get; set; }
        public int FaceCount { get; set; }
        public List<string> Signals { get; set; } = new();
    }
}
