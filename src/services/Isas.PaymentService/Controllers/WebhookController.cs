using System.Text.Json;
using Isas.PaymentService.Services;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using PayOS;
using PayOS.Exceptions;
using PayOS.Models.Webhooks;

namespace Isas.PaymentService.Controllers
{
    /// <summary>
    /// Webhook PayOS — KHÔNG qua gateway, KHÔNG [Authorize] (PayOS gọi trực tiếp, xác thực bằng chữ ký HMAC).
    /// Verify chữ ký TRƯỚC (bằng ChecksumKey qua SDK). Chỉ cộng credit khi Paid + đã verify (PAY-8). Luôn trả
    /// 200 khi payload hợp lệ (kể cả no-op idempotent / không khớp đơn) để PayOS ngừng retry.
    /// </summary>
    [ApiController]
    [AllowAnonymous]
    [Route("webhook")]
    public class WebhookController : ControllerBase
    {
        private readonly PayOSClient _payos;
        private readonly IWebhookService _webhooks;
        private readonly ILogger<WebhookController> _logger;

        public WebhookController(PayOSClient payos, IWebhookService webhooks, ILogger<WebhookController> logger)
        {
            _payos = payos;
            _webhooks = webhooks;
            _logger = logger;
        }

        [HttpPost("payos")]
        public async Task<IActionResult> PayOsAsync(CancellationToken ct = default)
        {
            // Đọc raw body (nguồn để lưu bằng chứng đối soát nguyên trạng) rồi mới deserialize.
            string raw;
            using (var reader = new StreamReader(Request.Body))
                raw = await reader.ReadToEndAsync(ct);

            Webhook? webhook;
            try
            {
                webhook = JsonSerializer.Deserialize<Webhook>(raw);
            }
            catch (JsonException)
            {
                webhook = null;
            }

            if (webhook is null || webhook.Data is null)
                return BadRequest(new { message = "Invalid webhook payload." });

            // 1) VERIFY chữ ký TRƯỚC (SDK tính lại HMAC-SHA256 từ data bằng ChecksumKey). Sai chữ ký →
            //    ném InvalidSignatureException/WebhookException → 400, KHÔNG xử lý (chống giả mạo cộng credit).
            WebhookData data;
            try
            {
                data = await _payos.Webhooks.VerifyAsync(webhook);
            }
            catch (InvalidSignatureException)
            {
                _logger.LogWarning("Webhook PayOS sai chữ ký — từ chối.");
                return BadRequest(new { message = "Invalid signature." });
            }
            catch (WebhookException ex)
            {
                _logger.LogWarning(ex, "Webhook PayOS không hợp lệ — từ chối.");
                return BadRequest(new { message = "Invalid webhook." });
            }

            // 2) Chỉ xử lý khi thành công (success). Sự kiện khác (fail/cancel) → không cộng credit, vẫn 200.
            if (!webhook.Success)
            {
                _logger.LogInformation(
                    "Webhook PayOS orderCode={OrderCode} không phải Paid (success=false) — bỏ qua cộng credit.",
                    data.OrderCode);
                return Ok(new { message = "Ignored (not paid)." });
            }

            // 3) Áp — idempotent theo payos_order_code (PAY-8). gateway_txn_id = reference giao dịch ngân hàng.
            var outcome = await _webhooks.ApplyPaidWebhookAsync(data.OrderCode, data.Reference, raw, ct);
            _logger.LogInformation(
                "Webhook PayOS orderCode={OrderCode} → {Outcome}", data.OrderCode, outcome);

            // Luôn 200 khi đã nhận hợp lệ (kể cả AlreadyProcessed/OrderNotFound) — PayOS ngừng retry.
            return Ok(new { outcome = outcome.ToString() });
        }
    }
}
