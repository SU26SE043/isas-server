using Isas.InterviewService.Services;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;

namespace Isas.InterviewService.Controllers;

/// <summary>
/// F21 — AIService nạp các mảnh prompt đã tuỳ biến từ đây.
///
/// <para>GEN-1: internal, KHÔNG qua gateway, gác bằng <c>X-Internal-Token</c> (không JWT).</para>
///
/// <para><b>Vì sao AIService KÉO chứ không phải .NET ĐẨY:</b> GEN-4 cấm AIService ghi DB và nó
/// không có kết nối DB nào, nên prompt buộc phải nằm ở service .NET. Hai đường còn lại đều tệ
/// hơn: (a) caller truyền prompt hoàn chỉnh xuống — không làm được, vì builder là template CÓ
/// NỘI SUY (dựng rubric_block từ levels+anchors, khối chỉ số từ số đo F11), muốn caller dựng
/// sẵn thì phải bê cả 28KB logic prompt sang .NET, tức là viết lại chứ không phải làm registry;
/// (b) mount file/ConfigMap — không có đường quản trị nên không đạt "admin sửa qua UI", và
/// aiworker chạy trên Mac ngoài compose server.</para>
///
/// <para>Trả về CHỈ phần đã tuỳ biến. Khoá không có trong response ⇒ AIService dùng bản mặc
/// định trong <c>prompts.py</c>. Bảng rỗng ⇒ response rỗng ⇒ hệ thống chạy đúng như trước F21.</para>
/// </summary>
[ApiController]
public class InternalPromptController(
    PromptTemplateService service,
    IConfiguration config,
    ILogger<InternalPromptController> logger) : ControllerBase
{
    [HttpGet("internal/prompts")]
    [AllowAnonymous]
    public async Task<IActionResult> GetActiveAsync(
        [FromHeader(Name = "X-Internal-Token")] string? token, CancellationToken ct)
    {
        var expected = config["Internal:Token"];
        if (string.IsNullOrEmpty(expected) || token != expected)
        {
            logger.LogWarning("F21: nạp prompt bị từ chối — X-Internal-Token sai.");
            return Unauthorized(new { error = "Invalid internal token" });
        }

        return Ok(new
        {
            templates = await service.GetActiveMapAsync(ct),
            // Con dấu để .NET đóng lên answer_scores và AIService ghi kèm log — hai bên nói về
            // cùng một phiên bản thước đo.
            promptVersion = await service.GetPromptVersionStampAsync(ct),
        });
    }
}
