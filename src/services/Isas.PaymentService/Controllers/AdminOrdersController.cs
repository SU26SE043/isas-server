using System.Security.Claims;
using Isas.PaymentService.DTOs;
using Isas.PaymentService.Services;
using Isas.Shared.Pagination;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using PaymentService.Models;
using static Isas.PaymentService.DTOs.OrderRequest;

namespace Isas.PaymentService.Controllers
{
    /// <summary>
    /// AUTH-7 — PlatformAdmin oversight (read-only, cross-owner). Xem MỌI đơn toàn hệ thống
    /// (không lọc theo chủ ví caller). Admin-gated trong service sở hữu dữ liệu. Không mutation.
    /// Route "admin" → gateway strip /api/v1/payment → /api/v1/payment/admin/orders (khớp admin/invoices/close).
    /// </summary>
    [ApiController]
    [Route("admin")]
    [Authorize(Roles = "Admin")]
    public class AdminOrdersController : ControllerBase
    {
        private readonly IOrderService _order;
        private readonly IRefundService _refund;

        public AdminOrdersController(IOrderService order, IRefundService refund)
        {
            _order = order;
            _refund = refund;
        }

        // GET /payment/admin/orders — mọi đơn (mới nhất trước; keyset-paged DB8).
        // ?status= lọc theo OrderStatus (numeric: 1=Pending..6=Refunded); ?ownerType= lọc Org/User.
        // ?refundSettlement=1(Pending)|2(Settled) lọc đơn hoàn theo trạng thái CHUYỂN TIỀN (chờ/đã chuyển).
        // ?limit= (mặc định/tối đa 500) + ?cursor= (opaque) để phân trang; next-cursor trả ở header
        // X-Next-Cursor (vắng = hết trang). Body giữ nguyên mảng JSON (backward-compat cho FE).
        [HttpGet("orders")]
        public async Task<ActionResult<List<AdminOrderListItem>>> ListOrders(
            [FromQuery] OrderStatus? status = null, [FromQuery] OwnerType? ownerType = null,
            [FromQuery] RefundSettlementFilter? refundSettlement = null,
            [FromQuery] string? cursor = null, [FromQuery] int? limit = null, CancellationToken ct = default)
        {
            var page = await _order.ListAllOrdersAsync(status, ownerType, refundSettlement, cursor, limit, ct);
            if (page.NextCursor is not null)
                Response.Headers[KeysetPaging.NextCursorHeader] = page.NextCursor;
            return Ok(page.Items);
        }

        // F18 — POST /payment/admin/orders/{id}/refund: hoàn tiền đơn mua credit.
        // 200 = đã hoàn (kể cả lần gọi lặp — idempotent) · 400 loại đơn không hỗ trợ · 404 không có đơn
        // · 409 đơn chưa Paid / ví không đủ credit thu hồi / ví vừa đổi giữa chừng.
        //
        // ⚠ Đây là mutation TIỀN duy nhất trong service không do cổng thanh toán khởi xướng. Người thực
        // hiện lấy từ JWT chứ KHÔNG nhận từ body: cho client tự khai "tôi là admin nào" thì cột
        // refunded_by thành lời khai của chính kẻ cần bị truy trách nhiệm.
        [HttpPost("orders/{id:guid}/refund")]
        public async Task<ActionResult<RefundOrderResponse>> RefundOrder(
            Guid id, [FromBody] RefundOrderRequest req, CancellationToken ct = default)
        {
            var sub = User.FindFirstValue(ClaimTypes.NameIdentifier);
            if (!Guid.TryParse(sub, out var adminId))
                return Forbid();

            var result = await _refund.RefundOrderAsync(
                id, adminId, req.Reason, req.GatewayRef, req.AllowPartialClawback, req.SettledNow, ct);

            return result.Outcome switch
            {
                RefundOutcome.OrderNotFound => NotFound(new { message = "Không tìm thấy đơn." }),
                RefundOutcome.NotPaid => Conflict(new
                {
                    message = "Đơn chưa ở trạng thái Paid — chưa thu tiền thì không có gì để hoàn."
                }),
                RefundOutcome.UnsupportedKind => BadRequest(new
                {
                    message = "Chỉ hoàn được đơn mua gói credit (CreditPack). Hoàn hoá đơn postpaid hoặc "
                            + "thu hồi kỳ thuê bao là nghiệp vụ riêng, chưa hỗ trợ ở đây."
                }),
                RefundOutcome.InsufficientCredits => Conflict(new
                {
                    message = "Ví không còn đủ credit để thu hồi trọn khoản đã bán. Gọi lại với "
                            + "allowPartialClawback=true nếu chấp nhận thu hồi một phần.",
                    creditsPurchased = result.CreditsPurchased,
                    clawbackPossible = result.CreditsClawedBack,
                    clawbackCeiling = result.ClawbackCeiling
                }),
                RefundOutcome.WalletChanged => Conflict(new
                {
                    message = "Số dư ví vừa thay đổi giữa lúc hoàn (có buổi thi vừa giữ/tiêu credit). "
                            + "Đơn KHÔNG bị đổi trạng thái — thử lại."
                }),
                _ => Ok(RefundOrderResponse.From(result))
            };
        }

        // POST /payment/admin/orders/{id}/refund/payout: CHI tiền hoàn về tài khoản người đã trả, qua
        // kênh chi payOS. Thay thao tác chuyển tay — nhưng KHÔNG bỏ nó: mọi ca không tự động được vẫn
        // rơi về /refund/settle.
        // 202 = đã gửi lệnh, đang chờ ngân hàng · 200 = đã chuyển xong và đóng dấu · 404 không có đơn
        // · 409 đơn chưa Refunded / đã settle / lệnh trước đã hỏng / tên người nhận không khớp
        // · 422 không dựng được đích chuyển · 503 chưa bật hoặc ví chi không đủ.
        [HttpPost("orders/{id:guid}/refund/payout")]
        public async Task<ActionResult<RefundPayoutResponse>> PayoutRefund(
            Guid id, CancellationToken ct = default)
        {
            var sub = User.FindFirstValue(ClaimTypes.NameIdentifier);
            if (!Guid.TryParse(sub, out var adminId))
                return Forbid();

            var result = await _refund.InitiateRefundPayoutAsync(id, adminId, ct);
            var body = RefundPayoutResponse.From(result);

            return result.Outcome switch
            {
                RefundPayoutOutcome.OrderNotFound => NotFound(new { message = "Không tìm thấy đơn." }),
                RefundPayoutOutcome.NotRefunded => Conflict(new
                {
                    message = "Đơn chưa được hoàn — quyết định hoàn phải có trước khi chuyển tiền."
                }),
                RefundPayoutOutcome.AlreadySettled => Conflict(new
                {
                    message = "Đơn đã được đóng dấu chuyển tiền — không chuyển lại.",
                    refundSettledAt = result.RefundSettledAt,
                    payoutId = result.PayoutId
                }),
                RefundPayoutOutcome.NotEnabled => StatusCode(StatusCodes.Status503ServiceUnavailable, new
                {
                    message = result.Message ?? "Chi tiền hoàn tự động chưa được bật."
                }),
                RefundPayoutOutcome.DestinationUnresolved =>
                    UnprocessableEntity(new { message = result.Message }),
                RefundPayoutOutcome.OverCeiling => Conflict(new { message = result.Message }),
                RefundPayoutOutcome.InsufficientBalance =>
                    StatusCode(StatusCodes.Status503ServiceUnavailable, new { message = result.Message }),
                RefundPayoutOutcome.Rejected => Conflict(new { message = result.Message }),
                // Tiền đã đi nhưng nhiều khả năng tới nhầm người — trả 409 kèm mã lệnh để đối soát ngay,
                // KHÔNG trả 200 (200 ở đây sẽ đọc thành "hoàn tiền xong").
                RefundPayoutOutcome.NameMismatch => Conflict(new
                {
                    message = result.Message,
                    payoutId = result.PayoutId
                }),
                RefundPayoutOutcome.InFlight => Accepted(body),
                _ => Ok(body)
            };
        }

        // F18 — POST /payment/admin/orders/{id}/refund/settle: XÁC NHẬN đã chuyển tiền hoàn thật cho khách.
        // Đường TAY, giữ nguyên làm lối thoát cho mọi ca chi tự động không xử được (không dựng được đích,
        // vượt trần, payOS từ chối). KHÔNG đụng credit/status, chỉ đóng dấu mốc đối soát + ghi mã tham chiếu.
        // 200 = đã settle (kể cả idempotent) · 404 không có đơn · 409 đơn chưa Refunded.
        [HttpPost("orders/{id:guid}/refund/settle")]
        public async Task<ActionResult<SettleRefundResponse>> SettleRefund(
            Guid id, [FromBody] SettleRefundRequest req, CancellationToken ct = default)
        {
            var sub = User.FindFirstValue(ClaimTypes.NameIdentifier);
            if (!Guid.TryParse(sub, out var adminId))
                return Forbid();

            var result = await _refund.SettleRefundAsync(id, adminId, req.GatewayRef?.Trim(), ct);

            return result.Outcome switch
            {
                SettleOutcome.OrderNotFound => NotFound(new { message = "Không tìm thấy đơn." }),
                SettleOutcome.NotRefunded => Conflict(new
                {
                    message = "Đơn chưa được hoàn — chưa có dòng tiền ra nào để xác nhận đã chuyển."
                }),
                _ => Ok(SettleRefundResponse.From(result))
            };
        }
    }
}
