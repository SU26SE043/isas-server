using System.Security.Cryptography;
using System.Text;
using Isas.PaymentService.DTOs;
using Isas.PaymentService.Services;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;

namespace Isas.PaymentService.Controllers
{
    /// <summary>
    /// API nội bộ Campaign/Interview → Payment (GEN-1: KHÔNG qua gateway; bảo vệ bằng X-Internal-Token).
    /// P4 /reserve · P5 /consume; /release = P6.
    /// </summary>
    [ApiController]
    [Route("internal/credits")]
    public class InternalCreditsController : ControllerBase
    {
        private readonly ICreditAccountService _credits;
        private readonly IConfiguration _config;
        private readonly ILogger<InternalCreditsController> _logger;

        public InternalCreditsController(
            ICreditAccountService credits,
            IConfiguration config,
            ILogger<InternalCreditsController> logger)
        {
            _credits = credits;
            _config = config;
            _logger = logger;
        }

        // P4 — giữ chỗ 1 credit cho session. B2B: Campaign gọi owner=Org; B2C: Interview gọi owner=User.
        // AllowAnonymous: gọi máy-máy, xác thực bằng X-Internal-Token (không JWT).
        [HttpPost("reserve")]
        [AllowAnonymous]
        public async Task<IActionResult> ReserveAsync(
            [FromBody] CreditOpRequest req,
            [FromHeader(Name = "X-Internal-Token")] string? token,
            CancellationToken ct = default)
        {
            if (!IsValidInternalToken(token))
                return Unauthorized(new { error = "Invalid internal token" });

            if (req.OwnerId == Guid.Empty || req.SessionId == Guid.Empty)
                return BadRequest(new { error = "ownerId and sessionId are required" });

            var result = await _credits.ReserveAsync(req.OwnerType, req.OwnerId, req.SessionId, ct);

            return result.Outcome switch
            {
                // Hết credit / hạn mức → 402 (PAY-5), KHÔNG tạo session ở phía caller.
                ReserveOutcome.Insufficient =>
                    StatusCode(StatusCodes.Status402PaymentRequired, new { error = "Insufficient credits" }),
                // Reserved hoặc AlreadyReserved (idempotent) đều trả 200 { reservationId, reservedCredits }.
                _ => Ok(new ReserveResponse
                {
                    ReservationId = result.ReservationId!.Value,
                    ReservedCredits = result.ReservedCredits
                })
            };
        }

        // P5 — trừ thật 1 credit khi session được chấm (SessionScored). Idempotent/absorbing theo
        // sessionId (PAY-11): gọi lại / miss reserve → no-op 200 (tránh kẹt retry ở caller — §State machine).
        [HttpPost("consume")]
        [AllowAnonymous]
        public async Task<IActionResult> ConsumeAsync(
            [FromBody] CreditOpRequest req,
            [FromHeader(Name = "X-Internal-Token")] string? token,
            CancellationToken ct = default)
        {
            if (!IsValidInternalToken(token))
                return Unauthorized(new { error = "Invalid internal token" });

            // sessionId = khoá idempotency + tra reservation; owner lấy từ reservation nên không bắt buộc ở đây.
            if (req.SessionId == Guid.Empty)
                return BadRequest(new { error = "sessionId is required" });

            var result = await _credits.ConsumeAsync(req.SessionId, ct);

            if (result.Outcome != ConsumeOutcome.Consumed)
                _logger.LogInformation(
                    "Consume no-op cho session {SessionId}: {Outcome}", req.SessionId, result.Outcome);

            // Mọi outcome (kể cả no-op) → 200: consume best-effort, KHÔNG bắt caller retry (§State machine).
            return Ok(new { status = result.Outcome.ToString(), reservationId = result.ReservationId });
        }

        // P6 — nhả chỗ giữ khi session bỏ ngang/lỗi (SessionAbandoned). Hoàn reserved−1, remaining+1,
        // KHÔNG ghi ledger. Idempotent/absorbing theo sessionId (PAY-11): gọi lại / đã Consumed / miss
        // reserve → no-op 200 (KHÔNG hoàn oan, tránh kẹt retry ở caller — §State machine).
        [HttpPost("release")]
        [AllowAnonymous]
        public async Task<IActionResult> ReleaseAsync(
            [FromBody] CreditOpRequest req,
            [FromHeader(Name = "X-Internal-Token")] string? token,
            CancellationToken ct = default)
        {
            if (!IsValidInternalToken(token))
                return Unauthorized(new { error = "Invalid internal token" });

            // sessionId = khoá idempotency + tra reservation; owner lấy từ reservation nên không bắt buộc ở đây.
            if (req.SessionId == Guid.Empty)
                return BadRequest(new { error = "sessionId is required" });

            var result = await _credits.ReleaseAsync(req.SessionId, ct);

            if (result.Outcome != ReleaseOutcome.Released)
                _logger.LogInformation(
                    "Release no-op cho session {SessionId}: {Outcome}", req.SessionId, result.Outcome);

            // Mọi outcome (kể cả no-op) → 200: release best-effort, KHÔNG bắt caller retry (§State machine).
            return Ok(new { status = result.Outcome.ToString(), reservationId = result.ReservationId });
        }

        private bool IsValidInternalToken(string? token)
        {
            var expected = _config["Internal:Token"];
            // Fail-closed: token chưa cấu hình → từ chối hết (không mở toang). Loại luôn token null/rỗng
            // trước khi so khớp (FixedTimeEquals cần 2 span; guard sớm giữ nguyên hành vi cũ).
            if (string.IsNullOrEmpty(expected) || string.IsNullOrEmpty(token))
            {
                _logger.LogWarning("Reserve bị từ chối: X-Internal-Token sai/thiếu.");
                return false;
            }

            // So khớp HẰNG-THỜI-GIAN trên UTF-8 bytes — đây là ranh giới auth DUY NHẤT cho ghi tiền
            // (reserve/consume/release). `token != expected` rò rỉ timing (string compare thoát sớm ở
            // byte lệch đầu tiên) → kẻ tấn công có thể dò từng ký tự token nội bộ.
            var ok = CryptographicOperations.FixedTimeEquals(
                Encoding.UTF8.GetBytes(token), Encoding.UTF8.GetBytes(expected));
            if (!ok)
                _logger.LogWarning("Reserve bị từ chối: X-Internal-Token sai/thiếu.");
            return ok;
        }
    }
}
