using System.ComponentModel.DataAnnotations;

namespace Isas.InterviewService.DTOs;

/// <summary>
/// F21 — một mảnh prompt. <paramref name="Body"/> null ⇒ chưa ai tuỳ biến, AIService đang dùng
/// bản mặc định trong <c>prompts.py</c> (bản mặc định CỐ Ý không chép sang .NET — hai nguồn sự
/// thật cho cùng câu chữ, ở hai ngôn ngữ, sẽ lệch nhau ngay lần sửa đầu tiên). Từ 2026-09-16 bản
/// mặc định được KÉO về qua <c>GET /api/v1/prompt-defaults</c> của AIService để màn admin hiện —
/// vẫn một nguồn, chỉ thêm đường đọc.
/// </summary>
public record PromptTemplateResponse(
    string Key,
    int Version,
    string? Body,
    Guid? UpdatedBy,
    string? ChangeNote,
    DateTime? CreatedAt,
    /// <summary>
    /// Bản mặc định trong mã AIService (khe THÊM ⇒ chuỗi rỗng; khe THAY ⇒ chuỗi mẫu có thể chứa
    /// <c>{role}</c>/<c>{job_category}</c>). <c>null</c> = KHÔNG LẤY ĐƯỢC từ AIService lúc này
    /// (fail-open) — client phải nói "chưa hiện được", không suy thành "mặc định trống". Vẫn chỉ
    /// có MỘT nguồn sự thật (Python); Interview chỉ chuyển tiếp, không chép.
    /// </summary>
    string? DefaultBody = null);

public class UpdatePromptTemplateRequest
{
    [Required(ErrorMessage = "Nội dung prompt là bắt buộc.")]
    public string Body { get; set; } = null!;

    /// <summary>Vì sao sửa — hiện lên lịch sử. Không bắt buộc ở tầng schema để admin sửa gấp
    /// không bị chặn, nhưng UI nên đòi.</summary>
    public string? ChangeNote { get; set; }
}
