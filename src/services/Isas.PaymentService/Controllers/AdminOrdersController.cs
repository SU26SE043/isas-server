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

        public AdminOrdersController(IOrderService order)
        {
            _order = order;
        }

        // GET /payment/admin/orders — mọi đơn (mới nhất trước; keyset-paged DB8).
        // ?status= lọc theo OrderStatus (numeric: 1=Pending..5=Cancelled); ?ownerType= lọc Org/User.
        // ?limit= (mặc định/tối đa 500) + ?cursor= (opaque) để phân trang; next-cursor trả ở header
        // X-Next-Cursor (vắng = hết trang). Body giữ nguyên mảng JSON (backward-compat cho FE).
        [HttpGet("orders")]
        public async Task<ActionResult<List<OrderResponse>>> ListOrders(
            [FromQuery] OrderStatus? status = null, [FromQuery] OwnerType? ownerType = null,
            [FromQuery] string? cursor = null, [FromQuery] int? limit = null, CancellationToken ct = default)
        {
            var page = await _order.ListAllOrdersAsync(status, ownerType, cursor, limit, ct);
            if (page.NextCursor is not null)
                Response.Headers[KeysetPaging.NextCursorHeader] = page.NextCursor;
            return Ok(page.Items);
        }
    }
}
