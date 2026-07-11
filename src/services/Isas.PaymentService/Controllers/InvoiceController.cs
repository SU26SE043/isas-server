using Isas.PaymentService.Services;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using PaymentService.Models;
using System.Security.Claims;
using static Isas.PaymentService.DTOs.InvoiceRequest;
using static Isas.PaymentService.DTOs.OrderRequest;

namespace Isas.PaymentService.Controllers
{
    /// <summary>
    /// P8b — hóa đơn postpaid (payment.md §endpoints /me/invoices · /invoices/{id}/pay · /admin/invoices/close).
    /// Chủ ví lấy từ JWT (D15): claim org_id → Org (billing cấp tổ chức). Hóa đơn CHỈ Org (postpaid).
    /// </summary>
    [ApiController]
    public class InvoiceController : ControllerBase
    {
        private readonly IInvoiceService _invoices;

        public InvoiceController(IInvoiceService invoices)
        {
            _invoices = invoices;
        }

        // Chủ ví lấy từ JWT (D15) — giống OrderController: có claim org_id → Org (B2B), không → User (B2C).
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

        [HttpGet("me/invoices")]
        [Authorize]
        public async Task<ActionResult<List<InvoiceResponse>>> GetMyInvoicesAsync(CancellationToken ct = default)
        {
            var owner = GetOwner();
            if (owner is null)
                return Forbid();

            var invoices = await _invoices.GetInvoicesAsync(owner.Value.OwnerType, owner.Value.OwnerId, ct);
            return Ok(invoices);
        }

        [HttpGet("me/invoices/{id:guid}")]
        [Authorize]
        public async Task<ActionResult<InvoiceResponse>> GetMyInvoiceAsync(Guid id, CancellationToken ct = default)
        {
            var owner = GetOwner();
            if (owner is null)
                return Forbid();

            var invoice = await _invoices.GetInvoiceAsync(owner.Value.OwnerType, owner.Value.OwnerId, id, ct);
            if (invoice is null) return NotFound();

            return Ok(invoice);
        }

        // Tất toán hóa đơn (OrgAdmin, owner-scope). Trả CreateOrderResponse (link PayOS). Cộng "tất toán"
        // chỉ khi webhook Paid (không tin return-url). 404 = không tồn tại/chủ khác · 409 = đã Paid/Void.
        [HttpPost("invoices/{id:guid}/pay")]
        [Authorize]
        public async Task<ActionResult<OrderResponse>> PayInvoiceAsync(Guid id, CancellationToken ct = default)
        {
            var owner = GetOwner();
            if (owner is null)
                return Forbid();

            var result = await _invoices.PayInvoiceAsync(owner.Value.OwnerType, owner.Value.OwnerId, id, ct);
            return result.Outcome switch
            {
                PayInvoiceOutcome.Created => Ok(result.Order),
                PayInvoiceOutcome.NotPayable => Conflict(new { message = "Invoice is not payable (already Paid or Void)." }),
                _ => NotFound()
            };
        }

        // Chốt kỳ 1 org (PlatformAdmin — A5 bật role sau, hiện chỉ cần authenticated như PackageController).
        [HttpPost("admin/invoices/close")]
        [Authorize]
        //[Authorize(Roles = "PlatformAdmin")]
        public async Task<ActionResult<InvoiceResponse>> CloseBillingPeriodAsync(
            CloseBillingPeriodRequest request, CancellationToken ct = default)
        {
            var userId = User.FindFirstValue(ClaimTypes.NameIdentifier);
            if (string.IsNullOrWhiteSpace(userId))
                return Forbid();

            try
            {
                var invoice = await _invoices.CloseBillingPeriodAsync(
                    request.OrgId, request.PeriodStart, request.PeriodEnd, ct);
                return Ok(invoice);
            }
            catch (KeyNotFoundException ex) { return NotFound(new { message = ex.Message }); }
        }
    }
}
