using Isas.InterviewService.DTOs;
using Isas.InterviewService.Enums;
using Isas.InterviewService.Services;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;

namespace Isas.InterviewService.Controllers;

/// <summary>
/// Admin quản BỘ CHUẨN rubric B2C. AUTH-7: endpoint admin nằm trong service SỞ HỮU dữ liệu.
///
/// <para>Gateway đã có route <c>interview/admin/**</c> (R5) nên không phải sửa gateway.</para>
///
/// <para><c>?language=</c> tuỳ chọn, mặc định <c>vi</c>. Ngôn ngữ lạ → <b>400</b>; chưa có bộ nào cho
/// (nghề, ngôn ngữ) đó → <b>404</b> (seed chưa apply).</para>
/// </summary>
[ApiController]
[Route("api/admin/rubrics")]
[Authorize(Roles = "Admin")]
public class AdminRubricController(IAdminB2CRubricService service) : ControllerBase
{
    /// <summary>
    /// Ma trận trạng thái (nghề × ngôn ngữ) — bỏ trống <paramref name="language"/> để xem cả hai.
    /// Đây là thứ duy nhất trả lời "còn thiếu mốc ở tổ hợp nào".
    /// </summary>
    [HttpGet]
    [ProducesResponseType(typeof(IReadOnlyList<AdminRubricMatrixRow>), StatusCodes.Status200OK)]
    [ProducesResponseType(StatusCodes.Status400BadRequest)]
    public async Task<IActionResult> Matrix([FromQuery] string? language, CancellationToken ct)
    {
        try
        {
            return Ok(await service.GetMatrixAsync(language, ct));
        }
        catch (InvalidOperationException ex)
        {
            return BadRequest(new { message = ex.Message });
        }
    }

    /// <summary>Bộ đang hiệu lực của 1 (nghề, ngôn ngữ), kèm mốc điểm.</summary>
    [HttpGet("{jobCategory}")]
    [ProducesResponseType(typeof(AdminRubricResponse), StatusCodes.Status200OK)]
    [ProducesResponseType(StatusCodes.Status404NotFound)]
    public async Task<IActionResult> Get(
        JobCategory jobCategory, [FromQuery] string? language, CancellationToken ct)
    {
        try
        {
            var result = await service.GetAsync(jobCategory, language, ct);
            return result is null ? NotFound() : Ok(result);
        }
        catch (InvalidOperationException ex)
        {
            return BadRequest(new { message = ex.Message });
        }
    }

    /// <summary>
    /// Lưu nội dung mới (mô tả + mốc điểm). Không đổi gì so với bản đang chạy ⇒ <c>changed = false</c>
    /// và KHÔNG tạo phiên bản mới.
    ///
    /// <para>Áp cho mọi buổi luyện BẮT ĐẦU SAU thời điểm này; buổi đang dở giữ thước cũ nhờ con dấu
    /// <c>b2c_rubric_version</c>; điểm cũ KHÔNG chấm lại.</para>
    /// </summary>
    [HttpPut("{jobCategory}")]
    [ProducesResponseType(typeof(AdminRubricResponse), StatusCodes.Status200OK)]
    [ProducesResponseType(StatusCodes.Status400BadRequest)]
    [ProducesResponseType(StatusCodes.Status404NotFound)]
    public async Task<IActionResult> Replace(
        JobCategory jobCategory, [FromBody] UpsertAdminRubricRequest request,
        [FromQuery] string? language, CancellationToken ct)
    {
        try
        {
            var result = await service.ReplaceAsync(jobCategory, request, language, ct);
            return result is null ? NotFound() : Ok(result);
        }
        catch (InvalidOperationException ex)
        {
            // Giữ đúng loại exception → 400. ArgumentException sẽ rơi xuống catch(Exception) thành 500
            // với MỌI input sai (Interview không có exception handler toàn cục) — lỗi đã xảy ra ở F2b.
            return BadRequest(new { message = ex.Message });
        }
    }

    /// <summary>Quay về nội dung gốc trong code (mốc rỗng ⇒ dải mặc định) — THÊM phiên bản, không xoá.</summary>
    [HttpDelete("{jobCategory}")]
    [ProducesResponseType(typeof(AdminRubricResponse), StatusCodes.Status200OK)]
    [ProducesResponseType(StatusCodes.Status404NotFound)]
    public async Task<IActionResult> Reset(
        JobCategory jobCategory, [FromQuery] string? language, CancellationToken ct)
    {
        try
        {
            var result = await service.ResetAsync(jobCategory, language, ct);
            return result is null ? NotFound() : Ok(result);
        }
        catch (InvalidOperationException ex)
        {
            return BadRequest(new { message = ex.Message });
        }
    }

    /// <summary>Lịch sử phiên bản — append-only nên đây là dấu vết đầy đủ.</summary>
    [HttpGet("{jobCategory}/history")]
    [ProducesResponseType(typeof(IReadOnlyList<AdminRubricVersionItem>), StatusCodes.Status200OK)]
    public async Task<IActionResult> History(
        JobCategory jobCategory, [FromQuery] string? language, CancellationToken ct)
    {
        try
        {
            return Ok(await service.HistoryAsync(jobCategory, language, ct));
        }
        catch (InvalidOperationException ex)
        {
            return BadRequest(new { message = ex.Message });
        }
    }
}
