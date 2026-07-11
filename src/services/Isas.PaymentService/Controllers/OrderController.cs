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
        public OrderController(IOrderService order)
        {
            _order = order;
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
            var owner = GetOwner();
            if (owner is null)
                return Forbid();

            try
            {
                var order = await _order.CreateOrderAsync(owner.Value.OwnerType, owner.Value.OwnerId, request, ct);
                return CreatedAtAction(nameof(GetOrderAsync), new { id = order.Id }, order);
            }
            catch (KeyNotFoundException ex) { return NotFound(new { message = ex.Message }); }
            catch (InvalidOperationException ex) { return BadRequest(new { message = ex.Message }); }
        }

        [HttpGet("{id:guid}")]
        [Authorize]
        public async Task<ActionResult<OrderResponse>> GetOrderAsync(Guid id, CancellationToken ct = default)
        {
            var owner = GetOwner();
            if (owner is null)
                return Forbid();

            var order = await _order.GetOrderAsync(id, ct);
            if (order is null) return NotFound();

            // Chủ đơn chỉ xem đơn của chính mình (khớp owner_type + owner_id)
            if (order.OwnerType != owner.Value.OwnerType || order.OwnerId != owner.Value.OwnerId)
                return Forbid();

            return Ok(order);
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

            // Ownership check before cancelling
            var order = await _order.GetOrderAsync(id, ct);
            if (order is null) return NotFound();
            if (order.OwnerType != owner.Value.OwnerType || order.OwnerId != owner.Value.OwnerId)
                return Forbid();

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
