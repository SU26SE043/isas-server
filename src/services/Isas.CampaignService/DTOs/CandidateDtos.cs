namespace Isas.CampaignService.DTOs
{
    /// <summary>
    /// C13 — Kết quả sàng CV hàng loạt. <c>Received</c> = tổng file xử lý = Rejected + Filtered + Skipped.
    /// <c>Skipped</c> = số CV bị bỏ qua vì trùng email (không tạo row). AI chấm khớp (C14) chưa chạy ở đây.
    /// </summary>
    public class ScreenCandidatesResponse
    {
        public int Received { get; set; }
        public int Rejected { get; set; }
        public int Filtered { get; set; }
        public int Skipped { get; set; }
        public List<ScreenedCandidateItem> Candidates { get; set; } = new();   // các row đã tạo (bỏ Skipped)
    }

    public class ScreenedCandidateItem
    {
        public Guid Id { get; set; }
        public string? FullName { get; set; }
        public string? Email { get; set; }
        public string Status { get; set; } = null!;      // Filtered | Rejected
        public string? RejectReason { get; set; }
    }
}
