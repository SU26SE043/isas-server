using Isas.PaymentService.DTOs;
using Isas.PaymentService.Services;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using PaymentService.Models;
using System.Security.Claims;
using static Isas.PaymentService.DTOs.InvoiceRequest;
using static Isas.PaymentService.DTOs.OrderRequest;
using static Isas.PaymentService.Services.IInvoiceService;

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

        // A5 — hóa đơn CHỈ Org postpaid (payment.md §Invoice) → role Employer. HrMember vẫn xem được
        // (chỉ pay/close mới chặn HrMember qua A4 guard). B2C (Candidate) không có hóa đơn.
        [HttpGet("me/invoices")]
        [Authorize(Roles = "Employer")]
        public async Task<ActionResult<List<InvoiceResponse>>> GetMyInvoicesAsync(CancellationToken ct = default)
        {
            var owner = GetOwner();
            if (owner is null)
                return Forbid();

            var invoices = await _invoices.GetInvoicesAsync(owner.Value.OwnerType, owner.Value.OwnerId, ct);
            return Ok(invoices);
        }

        [HttpGet("me/invoices/{id:guid}")]
        [Authorize(Roles = "Employer")]
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
        [Authorize(Roles = "Employer")]
        public async Task<ActionResult<OrderResponse>> PayInvoiceAsync(Guid id, CancellationToken ct = default)
        {
            // A4 (AUTH-6) — HrMember không có quyền billing (tất toán hóa đơn = money-mutation) → 403.
            if (User.IsHrMember())
                return Forbid();

            var owner = GetOwner();
            if (owner is null)
                return Forbid();

            try
            {
                var result = await _invoices.PayInvoiceAsync(owner.Value.OwnerType, owner.Value.OwnerId, id, ct);
                return result.Outcome switch
                {
                    PayInvoiceOutcome.Created => Ok(result.Order),
                    PayInvoiceOutcome.NotPayable => Conflict(new { message = "Invoice is not payable (already Paid or Void)." }),
                    _ => NotFound()
                };
            }
            // BF3 — PayOS misconfig/upstream reject khi tạo link tất toán → 502 sạch.
            catch (PaymentGatewayException ex) { return StatusCode(StatusCodes.Status502BadGateway, new { message = ex.Message }); }
        }

        // Chốt kỳ 1 org — admin-only (payment.md §Admin). Role string "Admin" (AUTH-3 = PlatformAdmin).
        // A4 guard HrMember giữ lại (defense-in-depth; Admin role vốn không có org_role=HrMember).
        [HttpGet("admin/invoices/postpaid-overview")]
        [Authorize(Roles = "Admin")]
        public async Task<ActionResult<List<PostpaidOverviewRow>>> GetPostpaidOverviewAsync(CancellationToken ct = default)
        {
            // A4 (AUTH-6) — HrMember không có quyền billing → 403 (defense-in-depth, giống endpoint chốt kỳ).
            if (User.IsHrMember())
                return Forbid();

            return Ok(await _invoices.GetPostpaidOverviewAsync(ct));
        }

        [HttpPost("admin/invoices/close")]
        [Authorize(Roles = "Admin")]
        public async Task<ActionResult<InvoiceResponse>> CloseBillingPeriodAsync(
            CloseBillingPeriodRequest request, CancellationToken ct = default)
        {
            // A4 (AUTH-6) — HrMember không có quyền billing (chốt kỳ = money-mutation) → 403.
            if (User.IsHrMember())
                return Forbid();

            var userId = User.FindFirstValue(ClaimTypes.NameIdentifier);
            if (string.IsNullOrWhiteSpace(userId))
                return Forbid();

            var result = await _invoices.CloseBillingPeriodAsync(
                request.OrgId, request.PeriodStart, request.PeriodEnd, ct);

            return result.Outcome switch
            {
                CloseBillingPeriodOutcome.WalletMissing => NotFound(new
                {
                    message = $"Không có ví cho Org {request.OrgId}."
                }),
                CloseBillingPeriodOutcome.NotPostpaid => Conflict(new
                {
                    message = "Org này đang Prepaid — không có kỳ postpaid nào để chốt."
                }),
                CloseBillingPeriodOutcome.UnitPriceNotConfigured => Conflict(new
                {
                    message = "Billing:UnitPrice chưa cấu hình (=0) — không lập hóa đơn 0đ. " +
                               "Đặt Billing:UnitPrice > 0 rồi thử lại."
                }),
                _ => Ok(result.Invoice)
            };
        }
    }
}
