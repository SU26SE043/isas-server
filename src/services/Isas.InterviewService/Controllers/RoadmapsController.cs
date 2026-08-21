using System.Security.Claims;
using Isas.InterviewService.DTOs;
using Isas.InterviewService.Services;
using Isas.InterviewService.Services.Interfaces;
using Isas.Shared.Pagination;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;

namespace Isas.InterviewService.Controllers;

// BC12 (D20) — /api/v1/interview/practice/roadmaps (gateway strip → api/practice/roadmaps).
// POST/GET roadmap (BC12) + mở lesson (lý thuyết lazy) + /start luyện (BC14).
[ApiController]
[Route("api/practice/roadmaps")]
[Authorize(Roles = "Candidate")] // A5 — roadmap ôn tập B2C = Candidate.
public class RoadmapsController : ControllerBase
{
    private readonly IRoadmapService _service;
    private readonly IRoadmapLessonService _lessonService;   // BC14
    private readonly IRoadmapReportService _reportService;   // BC15
    private readonly ILogger<RoadmapsController> _logger;

    public RoadmapsController(
        IRoadmapService service,
        IRoadmapLessonService lessonService,
        IRoadmapReportService reportService,
        ILogger<RoadmapsController> logger)
    {
        _service = service;
        _lessonService = lessonService;
        _reportService = reportService;
        _logger = logger;
    }

    private bool TryGetCandidateId(out Guid candidateId)
    {
        var sub = User.FindFirstValue(ClaimTypes.NameIdentifier) ?? User.FindFirstValue("sub");
        return Guid.TryParse(sub, out candidateId);
    }

    // POST /roadmaps {jobCategory, level, cvId?} → 201 RoadmapResponse (không trừ credit).
    [HttpPost]
    [ProducesResponseType(typeof(RoadmapResponse), StatusCodes.Status201Created)]
    [ProducesResponseType(StatusCodes.Status400BadRequest)]
    [ProducesResponseType(StatusCodes.Status403Forbidden)]
    [ProducesResponseType(StatusCodes.Status404NotFound)]
    [ProducesResponseType(StatusCodes.Status502BadGateway)]
    public async Task<IActionResult> Create([FromBody] CreateRoadmapRequest request, CancellationToken ct)
    {
        if (!TryGetCandidateId(out var candidateId))
            return Unauthorized(new { error = "Không xác định được danh tính người dùng." });

        try
        {
            var result = await _service.CreateAsync(candidateId, request, ct);
            return Created($"/api/practice/roadmaps/{result.Id}", result);
        }
        catch (KeyNotFoundException ex)
        {
            return NotFound(new { error = ex.Message });
        }
        catch (UnauthorizedAccessException ex)
        {
            return StatusCode(StatusCodes.Status403Forbidden, new { error = ex.Message });
        }
        catch (AiServiceException ex)
        {
            _logger.LogWarning(ex, "AIService /generate-roadmap lỗi khi tạo roadmap cho {CandidateId}", candidateId);
            return StatusCode(StatusCodes.Status502BadGateway, new { error = "AIService gặp lỗi. Vui lòng thử lại sau." });
        }
        catch (InvalidOperationException ex)
        {
            return BadRequest(new { error = ex.Message });
        }
    }

    // GET /roadmaps/{id} → RoadmapResponse đầy đủ (chỉ chủ; khác chủ → 403; không có → 404).
    [HttpGet("{id:guid}")]
    [ProducesResponseType(typeof(RoadmapResponse), StatusCodes.Status200OK)]
    [ProducesResponseType(StatusCodes.Status403Forbidden)]
    [ProducesResponseType(StatusCodes.Status404NotFound)]
    public async Task<IActionResult> Get(Guid id, CancellationToken ct)
    {
        if (!TryGetCandidateId(out var candidateId))
            return Unauthorized(new { error = "Không xác định được danh tính người dùng." });

        try
        {
            var result = await _service.GetAsync(candidateId, id, ct);
            if (result is null)
                return NotFound(new { error = "Không tìm thấy roadmap này." });

            return Ok(result);
        }
        catch (UnauthorizedAccessException ex)
        {
            return StatusCode(StatusCodes.Status403Forbidden, new { error = ex.Message });
        }
    }

    /// <summary>
    /// GET /roadmaps → danh sách roadmap của chính user. Keyset-paged: `?limit=` (mặc định/tối đa 500)
    /// + `?cursor=` (opaque, lấy từ header `X-Next-Cursor` của trang trước; vắng header = hết trang).
    /// Body giữ nguyên mảng JSON nên client cũ không phải sửa gì.
    ///
    /// Item là <see cref="RoadmapSummaryResponse"/> — KHÔNG còn `milestones` (trước đây list kéo cả
    /// cây milestone→lesson). Cần cây đầy đủ → `GET /roadmaps/{id}`.
    /// </summary>
    [HttpGet]
    [ProducesResponseType(typeof(IReadOnlyList<RoadmapSummaryResponse>), StatusCodes.Status200OK)]
    public async Task<IActionResult> List(
        CancellationToken ct, [FromQuery] string? cursor = null, [FromQuery] int? limit = null)
    {
        if (!TryGetCandidateId(out var candidateId))
            return Unauthorized(new { error = "Không xác định được danh tính người dùng." });

        var page = await _service.ListAsync(candidateId, cursor, limit, ct);
        if (page.NextCursor is not null)
            Response.Headers[KeysetPaging.NextCursorHeader] = page.NextCursor;
        return Ok(page.Items);
    }

    /// <summary>
    /// BE-6 — PATCH /roadmaps/{id} → đổi tên lộ trình. Chỉ chủ sở hữu.
    ///
    /// Tên rỗng / toàn khoảng trắng / quá dài → 400 (KHÔNG âm thầm rơi về tên máy sinh — người dùng
    /// gõ tên toàn dấu cách phải biết vì sao tên mình biến mất). Trả về roadmap sau khi đổi, KHÔNG
    /// kèm nội dung lý thuyết bài học vì màn đổi tên không cần và payload đó rất nặng.
    /// </summary>
    [HttpPatch("{id:guid}")]
    [ProducesResponseType(typeof(RoadmapResponse), StatusCodes.Status200OK)]
    [ProducesResponseType(StatusCodes.Status400BadRequest)]
    [ProducesResponseType(StatusCodes.Status403Forbidden)]
    [ProducesResponseType(StatusCodes.Status404NotFound)]
    public async Task<IActionResult> Rename(Guid id, [FromBody] RenameRoadmapRequest request, CancellationToken ct)
    {
        if (!TryGetCandidateId(out var candidateId))
            return Unauthorized(new { error = "Không xác định được danh tính người dùng." });

        try
        {
            var result = await _service.RenameAsync(candidateId, id, request?.Name, ct);
            if (result is null)
                return NotFound(new { error = "Không tìm thấy roadmap này." });

            return Ok(result);
        }
        catch (UnauthorizedAccessException ex)
        {
            return StatusCode(StatusCodes.Status403Forbidden, new { error = ex.Message });
        }
        catch (InvalidOperationException ex)
        {
            return BadRequest(new { error = ex.Message });
        }
    }

    // GET /roadmaps/{id}/lessons/{lessonId} — mở lesson (lý thuyết lazy). theory null → sinh & lưu 1 lần.
    // BC14. Miễn phí. Chủ mới xem (khác chủ → 403; không có → 404); AI lỗi → 502.
    [HttpGet("{id:guid}/lessons/{lessonId:guid}")]
    [ProducesResponseType(typeof(LessonResponse), StatusCodes.Status200OK)]
    [ProducesResponseType(StatusCodes.Status403Forbidden)]
    [ProducesResponseType(StatusCodes.Status404NotFound)]
    [ProducesResponseType(StatusCodes.Status502BadGateway)]
    public async Task<IActionResult> OpenLesson(Guid id, Guid lessonId, CancellationToken ct)
    {
        if (!TryGetCandidateId(out var candidateId))
            return Unauthorized(new { error = "Không xác định được danh tính người dùng." });

        try
        {
            var result = await _lessonService.OpenLessonAsync(candidateId, id, lessonId, ct);
            return Ok(result);
        }
        catch (KeyNotFoundException ex)
        {
            return NotFound(new { error = ex.Message });
        }
        catch (UnauthorizedAccessException ex)
        {
            return StatusCode(StatusCodes.Status403Forbidden, new { error = ex.Message });
        }
        catch (AiServiceException ex)
        {
            _logger.LogWarning(ex, "AIService /generate-lesson-theory lỗi khi mở lesson {LessonId}", lessonId);
            return StatusCode(StatusCodes.Status502BadGateway,
                new { error = "AIService gặp lỗi. Vui lòng thử lại sau." });
        }
    }

    // POST /roadmaps/{id}/lessons/{lessonId}/start — bắt đầu luyện (reserve 1 credit; hết → 402 KHÔNG
    // tạo session). BC14. Đang Practicing/Done → 409 (resume, không reserve thêm); AI/Payment lỗi → 502.
    [HttpPost("{id:guid}/lessons/{lessonId:guid}/start")]
    [ProducesResponseType(typeof(PracticeSessionResponse), StatusCodes.Status201Created)]
    [ProducesResponseType(StatusCodes.Status400BadRequest)]
    [ProducesResponseType(StatusCodes.Status402PaymentRequired)]
    [ProducesResponseType(StatusCodes.Status403Forbidden)]
    [ProducesResponseType(StatusCodes.Status404NotFound)]
    [ProducesResponseType(StatusCodes.Status409Conflict)]
    [ProducesResponseType(StatusCodes.Status502BadGateway)]
    public async Task<IActionResult> StartLesson(Guid id, Guid lessonId, CancellationToken ct)
    {
        if (!TryGetCandidateId(out var candidateId))
            return Unauthorized(new { error = "Không xác định được danh tính người dùng." });

        try
        {
            var result = await _lessonService.StartLessonAsync(candidateId, id, lessonId, ct);
            return Created($"/api/practice/sessions/{result.Id}", result);
        }
        catch (KeyNotFoundException ex)
        {
            return NotFound(new { error = ex.Message });
        }
        catch (UnauthorizedAccessException ex)
        {
            return StatusCode(StatusCodes.Status403Forbidden, new { error = ex.Message });
        }
        catch (LessonAlreadyStartedException ex)
        {
            // 409: đang luyện/đã xong → resume session cũ (kèm sessionId nếu có), không tạo/reserve thêm.
            return Conflict(new { error = ex.Message, sessionId = ex.SessionId });
        }
        catch (InsufficientCreditException ex)
        {
            _logger.LogWarning(ex, "Ví không đủ credit để /start lesson {LessonId}", lessonId);
            return StatusCode(StatusCodes.Status402PaymentRequired, new { error = ex.Message });
        }
        catch (PaymentServiceException ex)
        {
            _logger.LogError(ex, "PaymentService lỗi khi reserve credit cho /start lesson {LessonId}", lessonId);
            return StatusCode(StatusCodes.Status502BadGateway,
                new { error = "Dịch vụ thanh toán tạm thời không phản hồi. Vui lòng thử lại sau." });
        }
        catch (InvalidOperationException ex)
        {
            // Sinh câu hỏi lỗi / CV không đọc được.
            _logger.LogWarning(ex, "Lỗi logic khi /start lesson {LessonId}", lessonId);
            return BadRequest(new { error = ex.Message });
        }
    }

    // POST /roadmaps/{id}/lessons/{lessonId}/retry — LÀM LẠI bài đã hoàn thành để nâng điểm.
    //
    // Endpoint RIÊNG chứ không nới /start: /start mang nghĩa "bắt đầu / tiếp tục", còn FE phải phân
    // biệt được "tiếp tục buổi dở" với "tạo buổi mới" để hiện đúng nút. Response cùng SHAPE với
    // /start (PracticeSessionResponse) nên FE dùng lại nguyên bộ mapper.
    //
    // 200 · 402 hết credit (KHÔNG tạo session) · 409 khi bài còn Theory hoặc đang Practicing ·
    // 404 ngoài quyền sở hữu · 502 AI/Payment lỗi.
    [HttpPost("{id:guid}/lessons/{lessonId:guid}/retry")]
    [ProducesResponseType(typeof(PracticeSessionResponse), StatusCodes.Status200OK)]
    [ProducesResponseType(StatusCodes.Status400BadRequest)]
    [ProducesResponseType(StatusCodes.Status402PaymentRequired)]
    [ProducesResponseType(StatusCodes.Status403Forbidden)]
    [ProducesResponseType(StatusCodes.Status404NotFound)]
    [ProducesResponseType(StatusCodes.Status409Conflict)]
    [ProducesResponseType(StatusCodes.Status502BadGateway)]
    public async Task<IActionResult> RetryLesson(Guid id, Guid lessonId, CancellationToken ct)
    {
        if (!TryGetCandidateId(out var candidateId))
            return Unauthorized(new { error = "Không xác định được danh tính người dùng." });

        try
        {
            var result = await _lessonService.RetryLessonAsync(candidateId, id, lessonId, ct);
            return Ok(result);
        }
        catch (KeyNotFoundException ex)
        {
            return NotFound(new { error = ex.Message });
        }
        catch (UnauthorizedAccessException ex)
        {
            return StatusCode(StatusCodes.Status403Forbidden, new { error = ex.Message });
        }
        catch (LessonRetryNotAllowedException ex)
        {
            // 409: chưa học lần nào (bấm Bắt đầu) / đang có buổi dở (tiếp tục buổi đó).
            return Conflict(new { error = ex.Message, sessionId = ex.SessionId });
        }
        catch (InsufficientCreditException ex)
        {
            _logger.LogWarning(ex, "Ví không đủ credit để làm lại lesson {LessonId}", lessonId);
            return StatusCode(StatusCodes.Status402PaymentRequired, new { error = ex.Message });
        }
        catch (PaymentServiceException ex)
        {
            _logger.LogError(ex, "PaymentService lỗi khi reserve credit để làm lại lesson {LessonId}", lessonId);
            return StatusCode(StatusCodes.Status502BadGateway,
                new { error = "Dịch vụ thanh toán tạm thời không phản hồi. Vui lòng thử lại sau." });
        }
        catch (InvalidOperationException ex)
        {
            _logger.LogWarning(ex, "Lỗi logic khi làm lại lesson {LessonId}", lessonId);
            return BadRequest(new { error = ex.Message });
        }
    }

    // GET /roadmaps/{id}/report — report roadmap. BC15. Active → interim (radar + levelEvaluation, kết luận
    // có thể rỗng/null); Completed → snapshot final_report + overallComment (không tính lại). Chủ mới xem.
    [HttpGet("{id:guid}/report")]
    [ProducesResponseType(typeof(RoadmapReportResponse), StatusCodes.Status200OK)]
    [ProducesResponseType(StatusCodes.Status403Forbidden)]
    [ProducesResponseType(StatusCodes.Status404NotFound)]
    public async Task<IActionResult> GetReport(Guid id, CancellationToken ct)
    {
        if (!TryGetCandidateId(out var candidateId))
            return Unauthorized(new { error = "Không xác định được danh tính người dùng." });

        try
        {
            var report = await _reportService.GetReportAsync(candidateId, id, ct);
            if (report is null)
                return NotFound(new { error = "Không tìm thấy roadmap này." });

            return Ok(report);
        }
        catch (UnauthorizedAccessException ex)
        {
            return StatusCode(StatusCodes.Status403Forbidden, new { error = ex.Message });
        }
    }
}
