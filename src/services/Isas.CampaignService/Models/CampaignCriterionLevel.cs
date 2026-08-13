namespace Isas.CampaignService.Models
{
    /// <summary>
    /// CAMP-16/17 — MỐC ĐIỂM của một tiêu chí campaign (B2B). Mỗi mốc = một bậc thang chấm:
    /// <c>score</c> là điểm được gán khi câu trả lời khớp <c>descriptor</c>.
    ///
    /// <para><b>Bảng con, CỐ Ý không phải jsonb.</b> jsonb không ép được <c>UNIQUE (criterion_id, score)</c>.
    /// Hai mốc trùng <c>score</c> làm cả hai đầu snap điểm về mức gần nhất trở nên KHÔNG XÁC ĐỊNH
    /// (Python <c>min(valid_levels, key=...)</c> và C# <c>ResolveLevel</c> chọn phần tử đầu theo thứ tự
    /// gặp phải) ⇒ E9 chấm sai mà không lỗi nào nổ. Bảng con cũng trùng khít shape <c>rubric_levels</c>
    /// bên Interview ⇒ materialize là map 1-1, không phải dịch cấu trúc.</para>
    ///
    /// <para><b>Không có cột <c>updated_by</c>.</b> Mốc ghi theo lối replace-all (xoá bộ cũ + thêm bộ mới
    /// trong cùng SaveChanges), nên cột đó chỉ nhân bản "người bấm Lưu lần cuối" N lần. Ai sửa thước đo
    /// nằm ở <c>campaigns.rubric_version_updated_by</c> + <c>audit_logs</c>.</para>
    /// </summary>
    public class CampaignCriterionLevel
    {
        public Guid Id { get; set; }
        public Guid CriterionId { get; set; }

        /// <summary>Điểm của mốc. Nguyên, ∈ [0, criterion.MaxScore], distinct trong cùng tiêu chí.</summary>
        public int Score { get; set; }

        /// <summary>
        /// Mô tả QUAN SÁT ĐƯỢC của mốc (ứng viên nói/làm gì), không phải tính từ đánh giá
        /// ("tốt"/"khá" chỉ là đổi tên con số). Dạng khuyến nghị: <c>CÓ: … | CÒN THIẾU: …</c>.
        /// </summary>
        public string Descriptor { get; set; } = null!;

        public DateTime CreatedAt { get; set; }
        public DateTime UpdatedAt { get; set; }

        // Navigation
        public CampaignCriterion Criterion { get; set; } = null!;
    }
}
