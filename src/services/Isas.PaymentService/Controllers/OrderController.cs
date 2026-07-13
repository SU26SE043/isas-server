using Isas.PaymentService.Services;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using PaymentService.Models;
using System.Security.Claims;
using static Isas.PaymentService.DTOs.OrderRequest;

namespace Isas.PaymentService.Controllers
{
    [ApiController]
    [Route("order")]
    public class OrderController : ControllerBase
    {
        private readonly IOrderService _order;
        private readonly IOrderStatusService _status;
        public OrderController(IOrderService order, IOrderStatusService status)
        {
            _order = order;
            _status = status;
        }

        // Chủ ví lấy từ JWT (D15): có claim org_id → Org (B2B, billing cấp tổ chức), không → User (B2C cá nhân).
        // Claim do AuthService phát: org_id (varchar) khi user thuộc org; sub = ClaimTypes.NameIdentifier (userId).
        private (OwnerType OwnerType, Guid OwnerId)? GetOwner()
        {
            var orgId = User.FindFirstValue("org_id");
            if (!string.IsNullOrWhiteSpace(orgId) && Guid.TryParse(orgId, out var oid))
                return (OwnerType.Org, oid);

            var userId = User.FindFirstValue(ClaimTypes.NameIdentifier);
            if (!string.IsNullOrWhiteSpace(userId) && Guid.TryParse(userId, out var uid))
                return (OwnerType.User, uid);

            return null;
        }

        [HttpPost]
        [Authorize]
        public async Task<ActionResult<OrderResponse>> CreateOrderAsync(CreateOrderRequest request, CancellationToken ct = default)
        {
            // A4 (AUTH-6) — HrMember không có quyền billing (mua pack = money-mutation) → 403.
            // B2C (không có org_role) + OrgAdmin qua guard xuống logic bình thường.
            if (User.IsHrMember())
                return Forbid();

            var owner = GetOwner();
            if (owner is null)
                return Forbid();

            try
            {
                var order = await _order.CreateOrderAsync(owner.Value.OwnerType, owner.Value.OwnerId, request, ct);
                // BF4 — route name tường minh: 'Async' suffix bị strip mặc định nên nameof(GetOrderAsync)
                // ('GetOrderAsync') KHÔNG khớp action 'GetOrder' → CreatedAtAction ném "No route matches".
                return CreatedAtRoute("GetOrderById", new { id = order.Id }, order);
            }
            catch (KeyNotFoundException ex) { return NotFound(new { message = ex.Message }); }
            catch (InvalidOperationException ex) { return BadRequest(new { message = ex.Message }); }
            // BF3 — PayOS misconfig/upstream reject → 502 sạch (không phải 500 stack thô).
            catch (PaymentGatewayException ex) { return StatusCode(StatusCodes.Status502BadGateway, new { message = ex.Message }); }
        }

        [HttpGet("{id:guid}", Name = "GetOrderById")]
        [Authorize]
        public async Task<ActionResult<OrderResponse>> GetOrderAsync(Guid id, CancellationToken ct = default)
        {
            var owner = GetOwner();
            if (owner is null)
                return Forbid();

            var order = await _order.GetOrderAsync(id, ct);
            if (order is null) return NotFound();

            // Owner-scope: đơn của chủ ví khác → 404 (BK15 — không lộ tồn tại đơn người khác; đồng nhất
            // với GET /order/{id}/status (P3) và các endpoint invoice owner-scope (P8b)). Order-not-exist
            // và other-owner PHẢI không phân biệt được từ ngoài.
            if (order.OwnerType != owner.Value.OwnerType || order.OwnerId != owner.Value.OwnerId)
                return NotFound();

            return Ok(order);
        }

        // P3 — FE active-polling: server chưa nhận webhook (order Pending) → đối soát PayOS NGAY (payment.md:145).
        // Owner-scope trong service: đơn không tồn tại HOẶC của chủ ví khác → 404 (không lộ đơn người khác).
        [HttpGet("{id:guid}/status")]
        [Authorize]
        public async Task<ActionResult<OrderStatusResponse>> GetOrderStatusAsync(Guid id, CancellationToken ct = default)
        {
            var owner = GetOwner();
            if (owner is null)
                return Forbid();

            var result = await _status.GetOrderStatusAsync(id, owner.Value.OwnerType, owner.Value.OwnerId, ct);
            if (result is null) return NotFound();

            return Ok(new OrderStatusResponse
            {
                OrderCode = result.OrderCode,
                Status = result.Status.ToString(),
                PaidAt = result.PaidAt
            });
        }

        [HttpGet("my-orders")]
        [Authorize]
        public async Task<ActionResult<List<OrderResponse>>> GetMyOrdersAsync(CancellationToken ct = default)
        {
            var owner = GetOwner();
            if (owner is null)
                return Forbid();

            var orders = await _order.GetOwnerOrdersAsync(owner.Value.OwnerType, owner.Value.OwnerId, ct);
            return Ok(orders);
        }

        [HttpDelete("{id:guid}")]
        [Authorize]
        public async Task<IActionResult> CancelOrderAsync(Guid id, CancellationToken ct = default)
        {
            var owner = GetOwner();
            if (owner is null)
                return Forbid();

            // Ownership check before cancelling — order-not-exist HOẶC của chủ ví khác → 404
            // (BK15 — không lộ tồn tại đơn người khác, thống nhất owner-scope order/invoice).
            var order = await _order.GetOrderAsync(id, ct);
            if (order is null) return NotFound();
            if (order.OwnerType != owner.Value.OwnerType || order.OwnerId != owner.Value.OwnerId)
                return NotFound();

            try
            {
                await _order.CancelOrderAsync(id, ct);
                return NoContent();
            }
            catch (KeyNotFoundException ex) { return NotFound(new { message = ex.Message }); }
            catch (InvalidOperationException ex) { return BadRequest(new { message = ex.Message }); }
        }
    }
}
