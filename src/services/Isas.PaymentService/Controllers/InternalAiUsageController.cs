using System.Security.Cryptography;
using System.Text;
using Isas.PaymentService.DTOs;
using Isas.PaymentService.Services;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;

namespace Isas.PaymentService.Controllers
{
    /// <summary>
    /// F22 (FR18) — AIService đẩy số liệu token về đây (GEN-1: KHÔNG qua gateway; bảo vệ bằng
    /// <c>X-Internal-Token</c>). Đây là hiện thực của cơ chế GEN-4: AIService không ghi DB, kết quả đi qua
    /// callback nội bộ.
    ///
    /// LUÔN TRẢ 2xx CHO CALLER HỢP LỆ. Ghi hỏng số liệu là chuyện của Payment, không phải chuyện của lượt
    /// chấm đang chạy: caller là AIService và nó gọi endpoint này NGAY SAU một lượt LLM đã tốn tiền — bắt nó
    /// retry/xử lý lỗi ở đó là mở đường cho một tính năng quan sát làm hỏng đường chấm (answer Failed ⇒ mất
    /// credit, PAY-13). Phía AIService cũng đã nuốt lỗi (app/usage.py), đây là lớp thứ hai.
    /// </summary>
    [ApiController]
    [Route("internal/ai-usage")]
    public class InternalAiUsageController : ControllerBase
    {
        private readonly IAiUsageService _usage;
        private readonly IConfiguration _config;
        private readonly ILogger<InternalAiUsageController> _logger;

        public InternalAiUsageController(IAiUsageService usage, IConfiguration config,
            ILogger<InternalAiUsageController> logger)
        {
            _usage = usage;
            _config = config;
            _logger = logger;
        }

        // AllowAnonymous: gọi máy-máy, xác thực bằng X-Internal-Token (không JWT) — mẫu InternalCreditsController.
        [HttpPost]
        [AllowAnonymous]
        public async Task<IActionResult> RecordAsync(
            [FromBody] RecordAiUsageRequest req,
            [FromHeader(Name = "X-Internal-Token")] string? token,
            CancellationToken ct = default)
        {
            if (!IsValidInternalToken(token))
                return Unauthorized(new { error = "Invalid internal token" });

            if (string.IsNullOrWhiteSpace(req.Operation))
                return BadRequest(new { error = "operation is required" });

            try
            {
                var id = await _usage.RecordAsync(req, ct);
                return Ok(new { id });
            }
            catch (Exception ex)
            {
                // 202: "đã nhận, chưa chắc lưu được". Trả 500 sẽ đẩy AIService vào đường xử lý lỗi cho một
                // việc thuần quan sát — xem chú thích class.
                _logger.LogError(ex, "F22: không ghi được usage cho {Operation}", req.Operation);
                return Accepted(new { status = "dropped" });
            }
        }

        // Bản sao có chủ đích của InternalCreditsController.IsValidInternalToken: KHÔNG refactor gộp trong
        // vòng này vì bản gốc là ranh giới auth của đường GHI TIỀN (reserve/consume/release) — đụng vào nó
        // để tiện cho một tính năng thống kê là đổi chác tồi. Gộp được, nhưng phải là task riêng.
        private bool IsValidInternalToken(string? token)
        {
            var expected = _config["Internal:Token"];
            // Fail-closed: chưa cấu hình token → từ chối hết (không mở toang).
            if (string.IsNullOrEmpty(expected) || string.IsNullOrEmpty(token))
            {
                _logger.LogWarning("F22: ai-usage bị từ chối — X-Internal-Token sai/thiếu.");
                return false;
            }

            // So khớp HẰNG-THỜI-GIAN: `!=` thoát sớm ở byte lệch đầu tiên ⇒ rò rỉ timing cho phép dò dần
            // token nội bộ — mà token này là token DÙNG CHUNG với đường ghi tiền.
            var ok = CryptographicOperations.FixedTimeEquals(
                Encoding.UTF8.GetBytes(token), Encoding.UTF8.GetBytes(expected));
            if (!ok)
                _logger.LogWarning("F22: ai-usage bị từ chối — X-Internal-Token sai/thiếu.");
            return ok;
        }
    }
}
