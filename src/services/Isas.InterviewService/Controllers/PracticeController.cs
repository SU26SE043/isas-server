using System.Security.Claims;
using System.Net;
using Amazon.S3;
using Isas.InterviewService.DTOs;
using Isas.InterviewService.Services;
using Isas.InterviewService.Services.Interfaces;
using Isas.Shared.Pagination;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;

namespace Isas.InterviewService.Controllers;

[ApiController]
[Route("api/practice/sessions")] // Giữ nguyên Route chuẩn này của ông
[Authorize(Roles = "Candidate")] // A5 — luyện phỏng vấn B2C = Candidate.
public class PracticeController : ControllerBase
{
    private readonly IPracticeService _practiceService;
    private readonly IQuestionSpeechService _questionSpeechService;
    private readonly ILogger<PracticeController> _logger;

    public PracticeController(
        IPracticeService practiceService,
        IQuestionSpeechService questionSpeechService,
        ILogger<PracticeController> logger)
    {
        _practiceService = practiceService;
        _questionSpeechService = questionSpeechService;
        _logger = logger;
    }

    /// <summary>
    /// Helper: Gom code lấy CandidateId từ Token cho gọn, dùng lại ở nhiều hàm
    /// </summary>
    private Guid GetCandidateId()
    {
        var sub = User.FindFirstValue(ClaimTypes.NameIdentifier) ?? User.FindFirstValue("sub");
        if (string.IsNullOrEmpty(sub) || !Guid.TryParse(sub, out var candidateId))
        {
            throw new UnauthorizedAccessException("Không xác định được danh tính người dùng.");
        }
        return candidateId;
    }

    /// <summary>SC3 — preview preset câu hỏi do server tính, dùng đúng luật tạo session.</summary>
    [HttpGet("/api/practice/session-options")]
    [ProducesResponseType(typeof(PracticeSessionOptionsResponse), StatusCodes.Status200OK)]
    [ProducesResponseType(StatusCodes.Status400BadRequest)]
    // `language` optional (null = vi) — PHẢI khớp `language` sẽ gửi khi tạo session, nếu không preview
    // dựng trên rubric khác bộ rubric của buổi thật (số tiêu chí nội dung là SÀN của số câu gốc).
    public async Task<IActionResult> GetSessionOptions(
        [FromQuery] string jobCategory, [FromQuery] string? language, CancellationToken ct)
    {
        try
        {
            return Ok(await _practiceService.GetSessionOptionsAsync(
                GetCandidateId(), jobCategory, language, ct));
        }
        catch (UnauthorizedAccessException ex)
        {
            return Unauthorized(new { error = ex.Message });
        }
        catch (InvalidOperationException ex)
        {
            return BadRequest(new { error = ex.Message });
        }
    }

    /// <summary>
    /// 1. Tạo phiên phỏng vấn mới (Gọi AI sinh câu hỏi)
    /// </summary>
    [HttpPost]
    [ProducesResponseType(typeof(PracticeSessionResponse), StatusCodes.Status201Created)]
    [ProducesResponseType(StatusCodes.Status400BadRequest)]
    [ProducesResponseType(StatusCodes.Status402PaymentRequired)]   // BC2: ví hết credit
    [ProducesResponseType(StatusCodes.Status429TooManyRequests)]
    [ProducesResponseType(StatusCodes.Status502BadGateway)]        // BC2: PaymentService down
    public async Task<IActionResult> CreateSession([FromBody] CreatePracticeSessionRequest request, CancellationToken ct)
    {

        try
        {
            var candidateId = GetCandidateId();
            _logger.LogInformation("Candidate {CandidateId} đang yêu cầu tạo Session phỏng vấn cho Job {JobCategory}", 
                candidateId, request.JobCategory);

            var sessionResult = await _practiceService.CreateSessionAsync(candidateId, request, ct);

            // Trả về HTTP 201 Created kèm dữ liệu Session
            return Created($"/api/practice/sessions/{sessionResult.Id}", sessionResult);
        }
        catch (UnauthorizedAccessException ex)
        {
            return Unauthorized(new { error = ex.Message });
        }
        catch (InsufficientCreditException ex)
        {
            // BC2: ví hết credit → 402, KHÔNG tạo session (reserve ném trước khi ghi row).
            _logger.LogWarning(ex, "Ví không đủ credit để tạo session luyện.");
            return StatusCode(StatusCodes.Status402PaymentRequired, new { error = ex.Message });
        }
        catch (PaymentServiceException ex)
        {
            // BC2: PaymentService không phản hồi → 502 (không tạo session; retry được).
            _logger.LogError(ex, "PaymentService lỗi khi reserve credit.");
            return StatusCode(StatusCodes.Status502BadGateway,
                new { error = "Dịch vụ thanh toán tạm thời không phản hồi. Vui lòng thử lại sau." });
        }
        catch (AiServiceException ex)
        {
            // AIService lỗi thật (transport/timeout/5xx khi sinh câu hỏi) → 502, KHÔNG phải 400: đây là
            // lỗi upstream, không phải lỗi request. Reserve credit đã được release ở service (P1-2/BK12).
            _logger.LogError(ex, "AIService lỗi khi sinh câu hỏi.");
            return StatusCode(StatusCodes.Status502BadGateway,
                new { error = "Dịch vụ AI tạm thời không phản hồi. Vui lòng thử lại sau." });
        }
        catch (InvalidOperationException ex)
        {
            // Bắt lỗi AI trả về rỗng, hoặc CV/JD không đọc được → 400 (lỗi input, không phải upstream).
            _logger.LogWarning(ex, "Lỗi logic khi tạo session.");
            return BadRequest(new { error = ex.Message });
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Lỗi nghiêm trọng khi tạo Practice Session");
            return StatusCode(500, new { error = "Hệ thống AI đang quá tải hoặc gặp lỗi. Vui lòng thử lại sau." });
        }
    }

    /// <summary>
    /// 2. Lấy danh sách lịch sử phỏng vấn của user (Đặt trước {sessionId} để không bị lỗi Route).
    /// DB31 — keyset-paged: `?limit=` (mặc định/tối đa 500) + `?cursor=` (opaque, lấy từ header
    /// `X-Next-Cursor` của trang trước; vắng header = hết trang). Body giữ nguyên mảng JSON nên
    /// client cũ không phải sửa gì — trước đây endpoint này trả lịch sử TRỌN ĐỜI trong 1 payload.
    /// `?status=` (vd Scored) + `?excludeCampaign=true` — OPT-IN cho wizard tạo roadmap chọn
    /// buổi làm baseline (RoadmapService.CreateAsync chỉ nhận buổi B2C đã Scored); vắng cả hai ⇒
    /// hành vi y hệt hôm nay.
    /// `?source=lesson|free` — OPT-IN, tách buổi sinh từ bài học lộ trình khỏi buổi luyện tự do
    /// (dữ liệu đã có sẵn qua `roadmap_lesson_attempts` / nhãn `lessonTitle`, chỉ chưa lọc được).
    /// Hai giá trị là PHÂN HOẠCH: `lesson` + `free` = đúng tập khi không lọc. Giá trị lạ → 400.
    /// </summary>
    [HttpGet("history")]
    [ProducesResponseType(typeof(IReadOnlyList<PracticeSessionSummary>), StatusCodes.Status200OK)]
    public async Task<IActionResult> GetHistory(
        CancellationToken ct, [FromQuery] string? cursor = null, [FromQuery] int? limit = null,
        [FromQuery] string? status = null, [FromQuery] bool? excludeCampaign = null,
        [FromQuery] string? source = null)
    {
        try
        {
            var candidateId = GetCandidateId();
            var page = await _practiceService.GetHistoryAsync(
                candidateId, cursor, limit, status, excludeCampaign, source, ct);
            if (page.NextCursor is not null)
                Response.Headers[KeysetPaging.NextCursorHeader] = page.NextCursor;
            return Ok(page.Items);
        }
        // ⚠ Nhánh này BẮT BUỘC có: `ValidateHistoryStatus` ném `InvalidOperationException` cho giá
        // trị lạ, mà action này trước đó CHỈ bắt `UnauthorizedAccessException` ⇒ thiếu nó thì siết
        // validate lại biến `?status=xyz` thành **500** thay vì 400 — tệ hơn hẳn fail-open. Đúng lớp
        // lỗi F2b (ném sai loại exception → rơi xuống catch-all → 500 với MỌI input sai).
        catch (InvalidOperationException ex)
        {
            return BadRequest(new { error = ex.Message });
        }
        catch (UnauthorizedAccessException ex)
        {
            return Unauthorized(new { error = ex.Message });
        }
    }

    /// <summary>
    /// 3. Lấy thông tin chi tiết một phiên phỏng vấn (Câu hỏi, bài nộp, điểm số)
    /// </summary>
    [HttpGet("{sessionId:guid}")]
    [ProducesResponseType(typeof(PracticeSessionResponse), StatusCodes.Status200OK)]
    [ProducesResponseType(StatusCodes.Status404NotFound)]
    public async Task<IActionResult> GetSession(Guid sessionId, CancellationToken ct)
    {
        try
        {
            var candidateId = GetCandidateId();
            var response = await _practiceService.GetSessionAsync(candidateId, sessionId, ct);
            
            if (response == null)
                return NotFound(new { error = "Không tìm thấy phiên phỏng vấn này." });

            return Ok(response);
        }
        catch (UnauthorizedAccessException ex)
        {
            return StatusCode(StatusCodes.Status403Forbidden, new { error = ex.Message });
        }
        catch (CapacityExceededException ex)
        {
            Response.Headers.RetryAfter = "60";
            return StatusCode(StatusCodes.Status429TooManyRequests, new
            {
                error = ex.Message, code = "platform_capacity_exceeded", retryAfterSeconds = 60
            });
        }
    }

    /// <summary>Phát hoặc tải bản ghi âm câu trả lời của chính candidate.</summary>
    [HttpGet("{sessionId:guid}/answers/{answerId:guid}/audio")]
    // Content-Type thật phụ thuộc định dạng ứng viên đã thu (webm trên Chrome, m4a trên iPhone…) nên khai đủ
    // tập cho OpenAPI. KHÔNG thêm [Produces] — lý do ở GetQuestionSpeech bên dưới.
    [ProducesResponseType(typeof(FileStreamResult), StatusCodes.Status200OK,
        "audio/webm", "audio/ogg", "audio/mpeg", "audio/mp4", "video/mp4", "audio/flac", "audio/wav")]
    [ProducesResponseType(StatusCodes.Status403Forbidden)]
    [ProducesResponseType(StatusCodes.Status404NotFound)]
    public async Task<IActionResult> GetAnswerAudio(
        Guid sessionId, Guid answerId, CancellationToken ct)
    {
        try
        {
            var audio = await _practiceService.GetAnswerAudioAsync(
                GetCandidateId(), sessionId, answerId, ct);
            if (audio is null)
                return NotFound(new { error = "Không tìm thấy bản ghi âm câu trả lời này." });

            return File(audio.Content, audio.ContentType, enableRangeProcessing: true);
        }
        catch (UnauthorizedAccessException ex)
        {
            return StatusCode(StatusCodes.Status403Forbidden, new { error = ex.Message });
        }
        catch (AmazonS3Exception ex) when (
            ex.StatusCode == HttpStatusCode.NotFound || ex.ErrorCode is "NoSuchKey")
        {
            _logger.LogWarning(ex, "Không tìm thấy object S3 của audio answer {AnswerId} trong session {SessionId}.",
                answerId, sessionId);
            return NotFound(new { error = "Bản ghi âm không còn trên hệ thống." });
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Lỗi tải audio answer {AnswerId} trong session {SessionId}.", answerId, sessionId);
            return StatusCode(StatusCodes.Status500InternalServerError,
                new { error = "Không thể tải bản ghi âm lúc này. Vui lòng thử lại sau." });
        }
    }

    /// <summary>
    /// 4. Chốt sổ và nộp bài phỏng vấn (Bắn RabbitMQ đi chấm)
    /// </summary>
    [HttpPost("{sessionId:guid}/submit")]
    [ProducesResponseType(StatusCodes.Status204NoContent)]
    [ProducesResponseType(StatusCodes.Status400BadRequest)]
    [ProducesResponseType(StatusCodes.Status404NotFound)]
    public async Task<IActionResult> SubmitSession(Guid sessionId, CancellationToken ct)
    {
        try
        {
            var candidateId = GetCandidateId();
            await _practiceService.SubmitSessionAsync(candidateId, sessionId, ct);
            
            return NoContent(); // HTTP 204
        }
        catch (KeyNotFoundException ex)
        {
            return NotFound(new { error = ex.Message });
        }
        catch (InvalidOperationException ex)
        {
            // Lỗi chưa nộp câu nào, hoặc đã nộp rồi...
            return BadRequest(new { error = ex.Message });
        }
        catch (UnauthorizedAccessException ex)
        {
            return StatusCode(StatusCodes.Status403Forbidden, new { error = ex.Message });
        }
    }

    /// <summary>
    /// 5. Đọc câu hỏi thành tiếng (TTS) — trả bytes mp3 để FE phát cho ứng viên nghe.
    /// Dùng chung cho B2C (luyện tập) và B2B (campaign); KHÔNG trừ credit (PAY-1).
    /// AIService cache audio theo nội dung câu hỏi nên câu trùng không tổng hợp lại.
    /// </summary>
    // ⚠ KHÔNG thêm [Produces("audio/mpeg")] ở đây. ProducesAttribute ghi đè
    // ObjectResult.ContentTypes cho MỌI kết quả của action — kể cả NotFound/ObjectResult
    // body JSON của nhánh 403/404/502 → không formatter nào ghi được JSON dưới audio/mpeg
    // → client nhận 406 thay vì mã lỗi thật. Content-Type 200 đã do File(...) tự đặt.
    [HttpGet("{sessionId:guid}/questions/{questionId:guid}/speech")]
    [ProducesResponseType(typeof(FileContentResult), StatusCodes.Status200OK, "audio/mpeg")]
    [ProducesResponseType(StatusCodes.Status403Forbidden)]
    [ProducesResponseType(StatusCodes.Status404NotFound)]
    [ProducesResponseType(StatusCodes.Status502BadGateway)]
    [ProducesResponseType(StatusCodes.Status504GatewayTimeout)]
    public async Task<IActionResult> GetQuestionSpeech(
        Guid sessionId, Guid questionId, CancellationToken ct)
    {
        try
        {
            var candidateId = GetCandidateId();
            var speech = await _questionSpeechService.GetQuestionSpeechAsync(
                candidateId, sessionId, questionId, ct);

            if (speech is null)
                return NotFound(new { error = "Không tìm thấy câu hỏi này trong buổi phỏng vấn." });

            return File(speech.Content, speech.ContentType);
        }
        catch (UnauthorizedAccessException ex)
        {
            // INT-11 — không phải buổi của mình (khớp tiền lệ GetSession).
            return StatusCode(StatusCodes.Status403Forbidden, new { error = ex.Message });
        }
        catch (AiServiceException ex)
        {
            // TTS chết/quá tải → 502/504. FE degrade về CHỈ HIỆN CHỮ — không được chặn luồng
            // phỏng vấn chỉ vì không đọc được thành tiếng.
            //
            // Tách 504 (hết giờ) khỏi 502 (AIService trả lỗi) vì client cần phân biệt để cư xử
            // khác: hết giờ thường là CHẬM lần đầu và thử lại là được, còn 502 thì thử lại ngay
            // cũng thế. Trước đây gộp một mã nên FE chỉ hiện được "có lỗi" (báo cáo 2026-08-15).
            _logger.LogError(ex, "AIService lỗi khi đọc câu hỏi thành tiếng (timeout={IsTimeout}).",
                ex.IsTimeout);
            return ex.IsTimeout
                ? StatusCode(StatusCodes.Status504GatewayTimeout,
                    new { error = "Dịch vụ đọc câu hỏi phản hồi chậm. Bạn thử lại sau giây lát nhé." })
                : StatusCode(StatusCodes.Status502BadGateway,
                    new { error = "Dịch vụ đọc câu hỏi tạm thời không phản hồi. Vui lòng thử lại sau." });
        }
    }
}
