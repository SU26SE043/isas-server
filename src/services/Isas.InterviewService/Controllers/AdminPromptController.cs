using System.Security.Claims;
using Isas.InterviewService.DTOs;
using Isas.InterviewService.Services;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;

namespace Isas.InterviewService.Controllers;

/// <summary>
/// F21 (FR17) — màn quản trị prompt. AUTH-7: endpoint admin nằm trong service SỞ HỮU dữ liệu,
/// không tách service riêng.
/// </summary>
[ApiController]
[Route("api/admin/prompts")]
[Authorize(Roles = "Admin")]
public class AdminPromptController(PromptTemplateService service) : ControllerBase
{
    private Guid ActorId =>
        Guid.TryParse(User.FindFirstValue(ClaimTypes.NameIdentifier) ?? User.FindFirstValue("sub"),
            out var id) ? id : Guid.Empty;

    /// <summary>Mọi mảnh sửa được (kể cả mảnh chưa ai sửa — body null = đang dùng bản mặc định).</summary>
    [HttpGet]
    public async Task<ActionResult<IReadOnlyList<PromptTemplateResponse>>> ListAsync(
        CancellationToken ct) => Ok(await service.ListAsync(ct));

    /// <summary>Lịch sử một mảnh — append-only nên đây là dấu vết đầy đủ ai đổi gì, khi nào.</summary>
    [HttpGet("{key}/history")]
    public async Task<ActionResult<IReadOnlyList<PromptTemplateResponse>>> HistoryAsync(
        string key, CancellationToken ct) => Ok(await service.HistoryAsync(key, ct));

    /// <summary>Sửa = tạo version mới. Lần sinh/chấm KẾ TIẾP dùng bản mới (sau khi cache
    /// AIService hết hạn — mặc định 60s), KHÔNG cần deploy.</summary>
    [HttpPut("{key}")]
    public async Task<ActionResult<PromptTemplateResponse>> UpdateAsync(
        string key, [FromBody] UpdatePromptTemplateRequest req, CancellationToken ct)
    {
        try
        {
            return Ok(await service.UpsertAsync(key, req.Body, ActorId, req.ChangeNote, ct));
        }
        catch (InvalidOperationException ex)
        {
            // PracticeController chỉ bắt InvalidOperationException→400; giữ đúng loại đó ở đây
            // để không rơi xuống catch(Exception) thành 500 (đúng bẫy F2b đã dính).
            return BadRequest(new { message = ex.Message });
        }
    }

    /// <summary>Quay về bản mặc định trong code. Giữ nguyên lịch sử (không hard-delete).</summary>
    [HttpDelete("{key}")]
    public async Task<IActionResult> ResetAsync(string key, CancellationToken ct)
    {
        try
        {
            return await service.ResetAsync(key, ct) ? NoContent() : NotFound();
        }
        catch (InvalidOperationException ex)
        {
            return BadRequest(new { message = ex.Message });
        }
    }
}
