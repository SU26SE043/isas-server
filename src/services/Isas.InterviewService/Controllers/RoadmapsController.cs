using System.Security.Claims;
using Isas.InterviewService.DTOs;
using Isas.InterviewService.Services;
using Isas.InterviewService.Services.Interfaces;
using Isas.Shared.Pagination;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;

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
        // 🔴 REC1-B7 review — `RoadmapService.CreateAsync` không còn kiểm sự TỒN TẠI của `req.CvId`
        // bằng `ReadOwnedParsedTextAsync` (gỡ ở đó, xem comment tại vị trí gọi). CvId vẫn được lưu
        // xuống `roadmaps.cv_id` (FK Restrict → file_records) — id không tồn tại chặn ở chính
        // `SaveChangesAsync` bằng `DbUpdateException`, mà TRƯỚC bản vá này không action nào bắt nó
        // ⇒ ASP.NET trả 500 mặc định (rò stack trace ở môi trường Development — repro độc lập bằng
        // probe test: `Microsoft.EntityFrameworkCore.DbUpdateException` / inner `SQLite Error 19:
        // 'FOREIGN KEY constraint failed'`). Bắt tại đây để khôi phục ĐÚNG hành vi 404 sạch trước
        // REC1-B7 — không dùng `catch (Exception)` chung chung vì sẽ nuốt luôn lỗi thật không liên
        // quan CvId (Roadmap không có concurrency token nào ở đây nên không có ca
        // `DbUpdateConcurrencyException`; `CvAnalysisId`/`PriorRoadmapId` KHÔNG được lưu ở bất kỳ
        // đâu nữa nên không thể là nguồn của FK khác tại call site này — đã verify bằng grep).
        catch (DbUpdateException ex)
        {
            _logger.LogWarning(ex, "CvId không tồn tại (FK vi phạm) khi tạo roadmap cho {CandidateId}", candidateId);
            return NotFound(new { error = "CV không tồn tại." });
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
    ///
    /// `?status=` (vd Completed) + `?hasFinalReport=true` — OPT-IN cho picker "chọn lộ trình đã hoàn
    /// tất" của wizard tạo lộ trình; vắng cả hai ⇒ hành vi y hệt hôm nay. Lọc chạy TRONG SQL trước
    /// khi cắt trang, nên không còn cảnh lộ trình hợp lệ nằm ngoài trang đầu thì biến mất khỏi
    /// dropdown. Dùng `hasFinalReport` chứ đừng dùng `status=Completed` cho picker — xem
    /// <see cref="RoadmapSummaryResponse.HasFinalReport"/>.
    /// </summary>
    [HttpGet]
    [ProducesResponseType(typeof(IReadOnlyList<RoadmapSummaryResponse>), StatusCodes.Status200OK)]
    [ProducesResponseType(StatusCodes.Status400BadRequest)]
    public async Task<IActionResult> List(
        CancellationToken ct, [FromQuery] string? cursor = null, [FromQuery] int? limit = null,
        [FromQuery] string? status = null, [FromQuery] bool? hasFinalReport = null)
    {
        if (!TryGetCandidateId(out var candidateId))
            return Unauthorized(new { error = "Không xác định được danh tính người dùng." });

        try
        {
            var page = await _service.ListAsync(candidateId, cursor, limit, status, hasFinalReport, ct);
            if (page.NextCursor is not null)
                Response.Headers[KeysetPaging.NextCursorHeader] = page.NextCursor;
            return Ok(page.Items);
        }
        // ⚠ Nhánh này BẮT BUỘC có: `ValidateRoadmapStatus` ném `InvalidOperationException` cho giá
        // trị lạ, mà action này trước đó KHÔNG bắt gì cả ⇒ thiếu nó thì việc siết validate biến
        // `?status=xyz` thành **500** — tệ hơn hẳn fail-open. Đúng lớp lỗi F2b, và là đúng cái bẫy
        // đã phải vá một lần ở `PracticeController.GetHistory`.
        catch (InvalidOperationException ex)
        {
            return BadRequest(new { error = ex.Message });
        }
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
        catch (AiServiceException ex)
        {
            _logger.LogWarning(ex, "AIService lỗi khi /start lesson {LessonId}", lessonId);
            return StatusCode(StatusCodes.Status502BadGateway,
                new { error = "AIService gặp lỗi. Vui lòng thử lại sau." });
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
        catch (AiServiceException ex)
        {
            _logger.LogWarning(ex, "AIService lỗi khi làm lại lesson {LessonId}", lessonId);
            return StatusCode(StatusCodes.Status502BadGateway,
                new { error = "AIService gặp lỗi. Vui lòng thử lại sau." });
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

    // GET /roadmaps/{id}/milestones/{milestoneId}/score-report — PHẦN TÍNH đứng sau con số delta.
    //
    // Trang lộ trình hiện "So với chặng trước: −20% Giao tiếp & trình bày"; endpoint này trả về đúng
    // phép tính ra con số đó: điểm từng tiêu chí của chặng + các buổi đã cộng vào + mốc so.
    // Chỉ đọc, không trừ credit. Mọi chặng đều xem được — chặng chưa hoàn thành thì chỉ chưa có
    // delta chốt, phần "chặng này được bao nhiêu, từ những buổi nào" vẫn đầy đủ.
    //
    // 404 chặng không thuộc lộ trình / lộ trình không tồn tại · 403 lộ trình của người khác.
    [HttpGet("{id:guid}/milestones/{milestoneId:guid}/score-report")]
    [ProducesResponseType(typeof(MilestoneScoreReportResponse), StatusCodes.Status200OK)]
    [ProducesResponseType(StatusCodes.Status403Forbidden)]
    [ProducesResponseType(StatusCodes.Status404NotFound)]
    public async Task<IActionResult> GetMilestoneScoreReport(Guid id, Guid milestoneId, CancellationToken ct)
    {
        if (!TryGetCandidateId(out var candidateId))
            return Unauthorized(new { error = "Không xác định được danh tính người dùng." });

        try
        {
            var report = await _reportService.GetMilestoneScoreReportAsync(candidateId, id, milestoneId, ct);
            if (report is null)
                return NotFound(new { error = "Không tìm thấy chặng này trong lộ trình." });

            return Ok(report);
        }
        catch (UnauthorizedAccessException ex)
        {
            return StatusCode(StatusCodes.Status403Forbidden, new { error = ex.Message });
        }
    }
}
