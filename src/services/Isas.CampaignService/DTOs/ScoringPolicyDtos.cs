using System.Text.Json.Serialization;
using Isas.Shared.Scoring;

namespace Isas.CampaignService.DTOs
{
    /// <summary>
    /// SCP1 · HĐ-3 — hình dạng MỘT chính sách chấm điểm trên dây (camelCase qua JsonSerializerDefaults.Web).
    /// Dùng cho cả mẫu hệ thống lẫn bản của campaign. <c>kind</c> là chuỗi "Interview" | "CvScreening".
    /// <c>campaignId</c> KHÔNG có trong đối tượng HĐ-3 nên không trả (mẫu luôn null; bản campaign suy
    /// được từ đường dẫn).
    /// </summary>
    public sealed record ScoringPolicyResponse(
        Guid Id,
        string Kind,
        int Version,
        string EngineVersion,
        string Name,
        string? Description,
        string Expression,
        int? PassScorePct,
        Guid? SourceTemplateId,
        DateTime CreatedAt,
        Guid? CreatedBy);

    /// <summary>
    /// SCP1 · HĐ-2 — body của <c>POST /api/v1/campaign/{id}/scoring-policies/validate</c>.
    /// </summary>
    public sealed class ScoringPolicyValidateRequest
    {
        /// <summary>"Interview" | "CvScreening" (phân biệt hoa/thường). Sai/thiếu → <b>400</b> — đây là
        /// lỗi phong bì request, KHÔNG phải mã lỗi biểu thức của HĐ-2.</summary>
        public string? Kind { get; set; }

        /// <summary>Biểu thức theo ngôn ngữ HĐ-1. <c>null</c>/rỗng ⇒ <c>valid: false</c> + <c>SYNTAX_ERROR</c>.</summary>
        public string? Expression { get; set; }
    }

    /// <summary>
    /// SCP1 · HĐ-2 — kết quả kiểm. <c>valid: true</c> ⇒ chỉ <c>sampleScore</c>; <c>valid: false</c> ⇒
    /// chỉ <c>errors</c> (mảng <c>{ code, start, end }</c> — MÃ + khoảng ký tự nửa mở, KHÔNG câu chữ).
    /// </summary>
    public sealed record ScoringPolicyValidateResponse(
        bool Valid,
        [property: JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
        decimal? SampleScore,
        [property: JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
        IReadOnlyList<ScoringError>? Errors);
}
