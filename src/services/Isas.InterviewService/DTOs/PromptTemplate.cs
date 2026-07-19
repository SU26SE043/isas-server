using System.ComponentModel.DataAnnotations;

namespace Isas.InterviewService.DTOs;

/// <summary>
/// F21 — một mảnh prompt. <paramref name="Body"/> null ⇒ chưa ai tuỳ biến, AIService đang dùng
/// bản mặc định trong <c>prompts.py</c> (bản mặc định CỐ Ý không chép sang .NET — hai nguồn sự
/// thật cho cùng câu chữ, ở hai ngôn ngữ, sẽ lệch nhau ngay lần sửa đầu tiên).
/// </summary>
public record PromptTemplateResponse(
    string Key,
    int Version,
    string? Body,
    Guid? UpdatedBy,
    string? ChangeNote,
    DateTime? CreatedAt);

public class UpdatePromptTemplateRequest
{
    [Required(ErrorMessage = "Nội dung prompt là bắt buộc.")]
    public string Body { get; set; } = null!;

    /// <summary>Vì sao sửa — hiện lên lịch sử. Không bắt buộc ở tầng schema để admin sửa gấp
    /// không bị chặn, nhưng UI nên đòi.</summary>
    public string? ChangeNote { get; set; }
}
