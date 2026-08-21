using System.Security.Claims;
using Isas.InterviewService.DTOs;
using Isas.InterviewService.Services.Interfaces;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;

namespace Isas.InterviewService.Controllers;

/// <summary>
/// BC15 — admin chỉnh NGƯỠNG ĐẠT của từng cấp độ lộ trình. AUTH-7: endpoint admin nằm trong
/// service SỞ HỮU dữ liệu, không tách service riêng.
///
/// <para>Gateway đã có route <c>interview/admin/**</c> (R5) nên không phải sửa gateway.</para>
///
/// <para><b>Không hồi tố.</b> Lộ trình đã <c>Completed</c> đọc ngưỡng từ snapshot
/// <c>roadmaps.final_report</c> — sửa ở đây chỉ đổi report tính từ lúc sửa trở đi.</para>
/// </summary>
[ApiController]
[Route("api/admin/roadmap-thresholds")]
[Authorize(Roles = "Admin")]
public class AdminRoadmapThresholdController(IRoadmapThresholdService service) : ControllerBase
{
    private Guid ActorId =>
        Guid.TryParse(User.FindFirstValue(ClaimTypes.NameIdentifier) ?? User.FindFirstValue("sub"),
            out var id) ? id : Guid.Empty;

    /// <summary>
    /// Cả bộ ngưỡng: giá trị đang hiệu lực + mặc định của code + đã-bị-chỉnh-chưa, cho MỌI cấp độ
    /// (kể cả cấp chưa ai chỉnh). Thiếu ba thứ đó thì admin không biết mình đang sửa cái gì so với
    /// cái gì.
    /// </summary>
    [HttpGet]
    [ProducesResponseType(typeof(IReadOnlyList<RoadmapLevelThresholdResponse>), StatusCodes.Status200OK)]
    public async Task<ActionResult<IReadOnlyList<RoadmapLevelThresholdResponse>>> ListAsync(
        CancellationToken ct) => Ok(await service.ListAsync(ct));

    /// <summary>
    /// Đặt ngưỡng cho một hoặc nhiều cấp độ; cấp không nêu thì giữ nguyên. Có hiệu lực ngay với
    /// report tính sau đó (không cache, không cần deploy).
    /// </summary>
    [HttpPut]
    [ProducesResponseType(typeof(IReadOnlyList<RoadmapLevelThresholdResponse>), StatusCodes.Status200OK)]
    [ProducesResponseType(StatusCodes.Status400BadRequest)]
    public async Task<ActionResult<IReadOnlyList<RoadmapLevelThresholdResponse>>> UpdateAsync(
        [FromBody] UpdateRoadmapLevelThresholdsRequest req, CancellationToken ct)
    {
        try
        {
            return Ok(await service.UpsertAsync(req.Thresholds, ActorId, ct));
        }
        catch (InvalidOperationException ex)
        {
            // Giữ ĐÚNG loại exception này: các controller khác chỉ bắt InvalidOperationException→400,
            // ArgumentException sẽ rơi xuống catch(Exception) thành 500 (đúng bẫy F2b đã dính).
            return BadRequest(new { message = ex.Message });
        }
    }

    /// <summary>Bỏ phần ghi đè của một cấp → quay về mặc định trong code.</summary>
    [HttpDelete("{level}")]
    [ProducesResponseType(StatusCodes.Status204NoContent)]
    [ProducesResponseType(StatusCodes.Status400BadRequest)]
    [ProducesResponseType(StatusCodes.Status404NotFound)]
    public async Task<IActionResult> ResetAsync(string level, CancellationToken ct)
    {
        try
        {
            return await service.ResetAsync(level, ct) ? NoContent() : NotFound();
        }
        catch (InvalidOperationException ex)
        {
            return BadRequest(new { message = ex.Message });
        }
    }
}
