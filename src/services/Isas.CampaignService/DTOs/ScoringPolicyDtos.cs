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
}
