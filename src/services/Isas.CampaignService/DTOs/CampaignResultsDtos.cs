namespace Isas.CampaignService.DTOs
{
    // E5 — bảng kết quả + xếp hạng + pass/fail cho `GET /campaign/{id}/results`.
    // Đọc read-model local `campaign_rankings` (E4 upsert từ event SessionScored) → chỉ chứa ứng viên
    // đã `Scored` (CAMP-11); sắp giảm theo total_score, gán rank (đồng hạng), pass/fail so ngưỡng Employer.
    public class CampaignResultsResponse
    {
        public Guid CampaignId { get; set; }

        // Ngưỡng % Employer (campaigns.pass_score_pct). null = không auto → mọi result = null (HR quyết tay).
        public int? PassScorePct { get; set; }

        public int TotalCandidates { get; set; }

        public List<CampaignResultRow> Results { get; set; } = new();
    }

    public class CampaignResultRow
    {
        // Hạng dẫn xuất lúc đọc (doc §campaign_rankings: KHÔNG lưu cột rank). Đồng điểm → cùng rank (1,1,3).
        public int Rank { get; set; }
        public Guid CandidateId { get; set; }
        public Guid SessionId { get; set; }
        public decimal TotalScore { get; set; }
        // "Pass"/"Fail" so ngưỡng; null khi ngưỡng chưa đặt (HR quyết tay).
        public string? Result { get; set; }
        public DateTime ScoredAt { get; set; }
    }

    // E6 — kết quả xuất file (CSV/PDF) cho `GET /campaign/{id}/results/export`.
    // Controller trả `File(Content, ContentType, FileName)` (bám pattern DownloadCampaignFiles).
    public class CampaignResultExport
    {
        public byte[] Content { get; set; } = System.Array.Empty<byte>();
        public string ContentType { get; set; } = "text/csv";
        public string FileName { get; set; } = "results.csv";
    }
}
