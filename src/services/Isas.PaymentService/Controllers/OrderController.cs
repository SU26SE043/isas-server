using Isas.PaymentService.Services;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
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

        [HttpPost]
        [Authorize]
        public async Task<ActionResult<OrderResponse>> CreateOrderAsync(CreateOrderRequest request, CancellationToken ct = default)
        {
            var userId = User.FindFirstValue(ClaimTypes.NameIdentifier);
            if (string.IsNullOrWhiteSpace(userId))
                return Forbid();

            try
            {
                var order = await _order.CreateOrderAsync(Guid.Parse(userId), request, ct);
                return CreatedAtAction(nameof(GetOrderAsync), new { id = order.Id }, order);
            }
            catch (KeyNotFoundException ex) { return NotFound(new { message = ex.Message }); }
            catch (InvalidOperationException ex) { return BadRequest(new { message = ex.Message }); }
        }

        [HttpGet("{id:guid}")]
        [Authorize]
        public async Task<ActionResult<OrderResponse>> GetOrderAsync(Guid id, CancellationToken ct = default)
        {
            var userId = User.FindFirstValue(ClaimTypes.NameIdentifier);
            if (string.IsNullOrWhiteSpace(userId))
                return Forbid();

            var order = await _order.GetOrderAsync(id, ct);
            if (order is null) return NotFound();

            // Users can only see their own orders
            if (order.UserId != Guid.Parse(userId))
                return Forbid();

            return Ok(order);
        }

        [HttpGet("my-orders")]
        [Authorize]
        public async Task<ActionResult<List<OrderResponse>>> GetMyOrdersAsync(CancellationToken ct = default)
        {
            var userId = User.FindFirstValue(ClaimTypes.NameIdentifier);
            if (string.IsNullOrWhiteSpace(userId))
                return Forbid();

            var orders = await _order.GetUserOrdersAsync(Guid.Parse(userId), ct);
            return Ok(orders);
        }

        [HttpDelete("{id:guid}")]
        [Authorize]
        public async Task<IActionResult> CancelOrderAsync(Guid id, CancellationToken ct = default)
        {
            var userId = User.FindFirstValue(ClaimTypes.NameIdentifier);
            if (string.IsNullOrWhiteSpace(userId))
                return Forbid();

            // Ownership check before cancelling
            var order = await _order.GetOrderAsync(id, ct);
            if (order is null) return NotFound();
            if (order.UserId != Guid.Parse(userId)) return Forbid();

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