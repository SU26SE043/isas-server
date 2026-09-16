using System.Security.Claims;
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
public class AdminRubricController(
    IAdminB2CRubricService service,
    IAdminRubricPreviewService preview,
    IAiServiceTranscriber? transcriber = null) : ControllerBase
{
    /// <summary>Trần file ghi âm cho màn tự thử: ~3 phút webm/opus ≈ 2–3 MB; 15 MB là dư cho m4a.</summary>
    public const long TranscribeMaxBytes = 15 * 1024 * 1024;

    private Guid ActorId =>
        Guid.TryParse(User.FindFirstValue(ClaimTypes.NameIdentifier) ?? User.FindFirstValue("sub"),
            out var id) ? id : Guid.Empty;

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

    // ── Chấm thử ────────────────────────────────────────────────────────────────────────────

    /// <summary>
    /// AI soạn mốc cho cả bộ. KHÔNG ghi DB — xem/sửa rồi lưu qua đúng một cửa <c>PUT</c>, để luật bump
    /// phiên bản nằm ở một chỗ (mẫu CAMP-16). Lỗi AI → <b>502</b>, CỐ Ý không fallback dải mặc định:
    /// fallback nghĩa là admin thấy "Mức 3: Mức 3/5" và tin đó là do AI soạn.
    /// </summary>
    [HttpPost("{jobCategory}/levels/suggest")]
    [ProducesResponseType(typeof(AdminSuggestLevelsResponse), StatusCodes.Status200OK)]
    [ProducesResponseType(StatusCodes.Status404NotFound)]
    [ProducesResponseType(StatusCodes.Status502BadGateway)]
    public async Task<IActionResult> SuggestLevels(
        JobCategory jobCategory, [FromQuery] string? language, [FromQuery] string? seniority,
        CancellationToken ct)
    {
        try
        {
            return Ok(await preview.SuggestLevelsAsync(jobCategory, language, seniority, ct));
        }
        catch (KeyNotFoundException)
        {
            return NotFound();
        }
        catch (DownstreamServiceException ex)
        {
            return StatusCode(StatusCodes.Status502BadGateway, new { message = ex.Message });
        }
        catch (InvalidOperationException ex)
        {
            return BadRequest(new { message = ex.Message });
        }
    }

    /// <summary>
    /// Chạy một lượt chấm thử: AI viết 3 bài mẫu rồi chấm chính chúng bằng thước đo ĐANG LƯU.
    ///
    /// <para>Miễn phí, trần cứng 5 lượt THÀNH CÔNG cho mỗi (nghề, ngôn ngữ, phiên bản) → hết thì
    /// <b>429</b>. Đang có lượt chạy → <b>409</b>. Chưa khai mốc → <b>400</b> (chấm thử trên dải mặc
    /// định không kiểm chứng được gì).</para>
    /// </summary>
    [HttpPost("{jobCategory}/preview")]
    [ProducesResponseType(typeof(AdminRubricPreviewRunResponse), StatusCodes.Status200OK)]
    [ProducesResponseType(StatusCodes.Status400BadRequest)]
    [ProducesResponseType(StatusCodes.Status404NotFound)]
    [ProducesResponseType(StatusCodes.Status409Conflict)]
    [ProducesResponseType(StatusCodes.Status429TooManyRequests)]
    [ProducesResponseType(StatusCodes.Status502BadGateway)]
    public async Task<IActionResult> RunPreview(
        JobCategory jobCategory, [FromBody] AdminRubricPreviewRequest? request,
        [FromQuery] string? language, CancellationToken ct)
    {
        try
        {
            return Ok(await preview.RunAsync(
                ActorId, jobCategory, language, request ?? new AdminRubricPreviewRequest(), ct));
        }
        catch (KeyNotFoundException)
        {
            return NotFound();
        }
        catch (PreviewQuotaExceededException ex)
        {
            return StatusCode(StatusCodes.Status429TooManyRequests, new { message = ex.Message });
        }
        catch (DownstreamServiceException ex)
        {
            return StatusCode(StatusCodes.Status502BadGateway, new { message = ex.Message });
        }
        catch (InvalidOperationException ex) when (ex.Message.Contains("Đang có một lượt"))
        {
            return Conflict(new { message = ex.Message });
        }
        catch (InvalidOperationException ex)
        {
            return BadRequest(new { message = ex.Message });
        }
    }

    /// <summary>
    /// Chép lời một bản ghi âm rời cho màn "tự thử thước đo": admin nói vào mic → hệ chép lời + đo
    /// cách nói → bản chép hiện ra để sửa → rồi mới chấm (POST …/preview với <c>customAnswer</c> +
    /// <c>deliveryMetrics</c>). KHÔNG tốn lượt chấm thử, KHÔNG lưu gì (không buổi, không answer, không
    /// S3). Không có tiếng nói → 200 với <c>noSpeech=true</c>, không phải lỗi — đó là chuyện của bản ghi.
    /// </summary>
    [HttpPost("{jobCategory}/preview/transcribe")]
    [RequestSizeLimit(TranscribeMaxBytes)]
    [ProducesResponseType(typeof(AdminPreviewTranscribeResponse), StatusCodes.Status200OK)]
    [ProducesResponseType(StatusCodes.Status400BadRequest)]
    [ProducesResponseType(StatusCodes.Status502BadGateway)]
    public async Task<IActionResult> TranscribeForPreview(
        JobCategory jobCategory, IFormFile? file, [FromQuery] string? language, CancellationToken ct)
    {
        _ = jobCategory;   // giữ route đồng nhất với nhóm preview; chép lời không phụ thuộc nghề
        if (transcriber is null)
            return StatusCode(StatusCodes.Status502BadGateway, new { message = "Chưa cấu hình dịch vụ chép lời." });
        if (file is null || file.Length == 0)
            return BadRequest(new { message = "Thiếu file ghi âm." });
        if (file.Length > TranscribeMaxBytes)
            return BadRequest(new { message = $"File ghi âm quá lớn (trần {TranscribeMaxBytes / (1024 * 1024)} MB)." });
        var lang = string.IsNullOrWhiteSpace(language) ? "vi" : language.Trim().ToLowerInvariant();
        if (lang is not ("vi" or "en"))
            return BadRequest(new { message = "language phải là 'vi' hoặc 'en'." });

        try
        {
            await using var stream = file.OpenReadStream();
            var result = await transcriber.TranscribeAsync(
                stream, string.IsNullOrWhiteSpace(file.FileName) ? "answer.webm" : file.FileName,
                file.ContentType, lang, ct);
            return Ok(new AdminPreviewTranscribeResponse(
                result.Text, result.DeliveryMetrics, result.TranscriptEngine,
                NoSpeech: string.Equals(result.RejectReason, "no_speech", StringComparison.OrdinalIgnoreCase)));
        }
        catch (DownstreamServiceException ex)
        {
            return StatusCode(StatusCodes.Status502BadGateway, new { message = ex.Message });
        }
    }

    /// <summary>Lịch sử chấm thử (20 lượt gần nhất) — nguồn để so TRƯỚC/SAU khi sửa mốc.</summary>
    [HttpGet("{jobCategory}/preview")]
    [ProducesResponseType(typeof(IReadOnlyList<AdminRubricPreviewRunResponse>), StatusCodes.Status200OK)]
    public async Task<IActionResult> PreviewHistory(
        JobCategory jobCategory, [FromQuery] string? language, CancellationToken ct)
    {
        try
        {
            return Ok(await preview.HistoryAsync(jobCategory, language, ct));
        }
        catch (InvalidOperationException ex)
        {
            return BadRequest(new { message = ex.Message });
        }
    }
}
