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

        // E11b — HR chốt điểm cuối. Effective (đã áp override) = TotalScore/Result ở trên ĐÃ tính theo override;
        // các cột dưới lộ override thô để FE hiện badge "HR chỉnh" + điểm AI gốc.
        public decimal AiScore { get; set; }          // điểm AI gốc (snapshot, không đổi khi override)
        public decimal? OverrideScore { get; set; }
        public string? OverrideResult { get; set; }
        public string? OverrideNote { get; set; }
        public DateTime? OverriddenAt { get; set; }

        // SEC-4: cờ chống gian lận gom theo buổi (signal_type → count). Additive — mặc định rỗng
        // (campaign không bật anti-cheat / không có cờ → []), KHÔNG phá client cũ. HR đánh giá lại (không auto-hủy).
        public List<FlagDto> Flags { get; set; } = new();
    }

    // SEC-4: 1 loại cờ đã gom cho HR — Type=signal_type, Count=số lần trong buổi, Note=1 ghi chú đại diện (nếu có).
    public class FlagDto
    {
        public string Type { get; set; } = null!;
        public int Count { get; set; }
        public string? Note { get; set; }
    }

    // E11b — HR chốt/sửa điểm cuối. Note bắt buộc (ghi audit). Score/Result đều null = CLEAR override (về AI).
    public class OverrideResultRequest
    {
        public decimal? Score { get; set; }
        public string? Result { get; set; }   // "Pass" | "Fail" | null
        public string Note { get; set; } = null!;
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
