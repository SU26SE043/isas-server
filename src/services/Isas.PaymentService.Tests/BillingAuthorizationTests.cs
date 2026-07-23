using System.Security.Claims;
using Isas.PaymentService.Controllers;
using Isas.PaymentService.Services;
using Isas.Shared.Pagination;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc;
using Moq;
using PaymentService.Models;
using static Isas.PaymentService.DTOs.InvoiceRequest;
using static Isas.PaymentService.DTOs.OrderRequest;

namespace Isas.PaymentService.Tests;

/// <summary>
/// A4 (AUTH-4/AUTH-6) — HrMember KHÔNG có quyền billing → 403 (Forbid) trên các endpoint money-mutation:
/// <c>POST /order</c> (mua pack) · <c>POST /invoices/{id}/pay</c> (tất toán) · <c>POST /admin/invoices/close</c>
/// (chốt kỳ). OrgAdmin và B2C (không mang claim <c>org_role</c>) KHÔNG bị chặn (qua guard xuống logic).
/// Endpoint GET đọc (<c>GET /my-orders</c>) KHÔNG bị chặn. Test ở tầng controller (biên đọc claim),
/// kiểm tra OFFLINE bằng JWT claim (GEN-3, không gọi AuthService).
/// </summary>
public class BillingAuthorizationTests
{
    // ClaimsPrincipal với org_role tuỳ chọn. authenticationType != null → IsAuthenticated = true.
    private static ClaimsPrincipal Principal(Guid userId, Guid? orgId, string? orgRole)
    {
        var claims = new List<Claim> { new(ClaimTypes.NameIdentifier, userId.ToString()) };
        if (orgId is Guid g) claims.Add(new Claim("org_id", g.ToString()));
        if (orgRole is not null) claims.Add(new Claim("org_role", orgRole));
        return new ClaimsPrincipal(new ClaimsIdentity(claims, "Test"));
    }

    private static ClaimsPrincipal HrMember() => Principal(Guid.NewGuid(), Guid.NewGuid(), "HrMember");
    private static ClaimsPrincipal OrgAdmin() => Principal(Guid.NewGuid(), Guid.NewGuid(), "OrgAdmin");
    private static ClaimsPrincipal B2CUser() => Principal(Guid.NewGuid(), orgId: null, orgRole: null);

    private static T WithUser<T>(T controller, ClaimsPrincipal user) where T : ControllerBase
    {
        controller.ControllerContext = new ControllerContext
        {
            HttpContext = new DefaultHttpContext { User = user }
        };
        return controller;
    }

    // ---------- OrderController: POST /order (mua pack) ----------

    private static (OrderController ctrl, Mock<IOrderService> order) NewOrderController(ClaimsPrincipal user)
    {
        var order = new Mock<IOrderService>();
        order.Setup(s => s.CreateOrderAsync(It.IsAny<OwnerType>(), It.IsAny<Guid>(),
                It.IsAny<CreateOrderRequest>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(new OrderResponse { Id = Guid.NewGuid() });
        var ctrl = WithUser(new OrderController(order.Object, Mock.Of<IOrderStatusService>()), user);
        return (ctrl, order);
    }

    // HrMember mua pack → 403; service KHÔNG được gọi (chặn TRƯỚC khi chạm logic).
    [Fact]
    public async Task CreateOrder_HrMember_tra_403_khong_goi_service()
    {
        var (ctrl, order) = NewOrderController(HrMember());

        var result = await ctrl.CreateOrderAsync(new CreateOrderRequest { PackageId = Guid.NewGuid() });

        Assert.IsType<ForbidResult>(result.Result);
        order.Verify(s => s.CreateOrderAsync(It.IsAny<OwnerType>(), It.IsAny<Guid>(),
            It.IsAny<CreateOrderRequest>(), It.IsAny<CancellationToken>()), Times.Never);
    }

    // OrgAdmin mua pack → qua guard, tới logic (KHÔNG 403).
    [Fact]
    public async Task CreateOrder_OrgAdmin_khong_bi_chan()
    {
        var (ctrl, order) = NewOrderController(OrgAdmin());

        var result = await ctrl.CreateOrderAsync(new CreateOrderRequest { PackageId = Guid.NewGuid() });

        Assert.IsNotType<ForbidResult>(result.Result);
        Assert.IsType<CreatedAtRouteResult>(result.Result);   // BF4 — CreatedAtRoute("GetOrderById")
        order.Verify(s => s.CreateOrderAsync(It.IsAny<OwnerType>(), It.IsAny<Guid>(),
            It.IsAny<CreateOrderRequest>(), It.IsAny<CancellationToken>()), Times.Once);
    }

    // B2C (không có claim org_role) mua pack cá nhân → KHÔNG chặn (chỉ chặn đúng HrMember).
    [Fact]
    public async Task CreateOrder_B2C_khong_bi_chan()
    {
        var (ctrl, order) = NewOrderController(B2CUser());

        var result = await ctrl.CreateOrderAsync(new CreateOrderRequest { PackageId = Guid.NewGuid() });

        Assert.IsNotType<ForbidResult>(result.Result);
        Assert.IsType<CreatedAtRouteResult>(result.Result);   // BF4 — CreatedAtRoute("GetOrderById")
        order.Verify(s => s.CreateOrderAsync(It.IsAny<OwnerType>(), It.IsAny<Guid>(),
            It.IsAny<CreateOrderRequest>(), It.IsAny<CancellationToken>()), Times.Once);
    }

    // GET /my-orders (đọc) → HrMember KHÔNG bị chặn (guard chỉ áp money-mutation).
    [Fact]
    public async Task GetMyOrders_HrMember_khong_bi_chan()
    {
        var order = new Mock<IOrderService>();
        order.Setup(s => s.GetOwnerOrdersAsync(It.IsAny<OwnerType>(), It.IsAny<Guid>(),
                It.IsAny<OrderStatus?>(), It.IsAny<string?>(), It.IsAny<int?>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(KeysetPage<OrderResponse>.Empty);
        var ctrl = WithUser(new OrderController(order.Object, Mock.Of<IOrderStatusService>()), HrMember());

        var result = await ctrl.GetMyOrdersAsync();

        Assert.IsNotType<ForbidResult>(result.Result);
        Assert.IsType<OkObjectResult>(result.Result);
    }

    // ---------- OrderController: DELETE /order/{id} (huỷ đơn) ----------

    // Huỷ đơn = money-mutation (void link thanh toán của OrgAdmin) → HrMember PHẢI 403.
    // Regression: guard này từng thiếu (e2e 2026-07-18: hr@ DELETE /order/{id} → 204, đơn Pending→Failed).
    [Fact]
    public async Task CancelOrder_HrMember_tra_403_khong_goi_service()
    {
        var order = new Mock<IOrderService>();
        var ctrl = WithUser(new OrderController(order.Object, Mock.Of<IOrderStatusService>()), HrMember());

        var result = await ctrl.CancelOrderAsync(Guid.NewGuid());

        Assert.IsType<ForbidResult>(result);
        // Chặn TRƯỚC cả ownership-check → không đọc, không huỷ.
        order.Verify(s => s.GetOrderAsync(It.IsAny<Guid>(), It.IsAny<CancellationToken>()), Times.Never);
        order.Verify(s => s.CancelOrderAsync(It.IsAny<Guid>(), It.IsAny<CancellationToken>()), Times.Never);
    }

    // OrgAdmin huỷ đơn của chính mình → qua guard, tới logic (KHÔNG 403).
    [Fact]
    public async Task CancelOrder_OrgAdmin_khong_bi_chan()
    {
        var orgId = Guid.NewGuid();
        var user = Principal(Guid.NewGuid(), orgId, "OrgAdmin");
        var orderId = Guid.NewGuid();

        var order = new Mock<IOrderService>();
        order.Setup(s => s.GetOrderAsync(orderId, It.IsAny<CancellationToken>()))
            .ReturnsAsync(new OrderResponse { Id = orderId, OwnerType = OwnerType.Org, OwnerId = orgId });
        var ctrl = WithUser(new OrderController(order.Object, Mock.Of<IOrderStatusService>()), user);

        var result = await ctrl.CancelOrderAsync(orderId);

        Assert.IsNotType<ForbidResult>(result);
        Assert.IsType<NoContentResult>(result);
        order.Verify(s => s.CancelOrderAsync(orderId, It.IsAny<CancellationToken>()), Times.Once);
    }

    // ---------- InvoiceController: POST /invoices/{id}/pay + POST /admin/invoices/close ----------

    private static (InvoiceController ctrl, Mock<IInvoiceService> inv) NewInvoiceController(ClaimsPrincipal user)
    {
        var inv = new Mock<IInvoiceService>();
        inv.Setup(s => s.PayInvoiceAsync(It.IsAny<OwnerType>(), It.IsAny<Guid>(), It.IsAny<Guid>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(PayInvoiceResult.Created(new OrderResponse { Id = Guid.NewGuid() }));
        inv.Setup(s => s.CloseBillingPeriodAsync(It.IsAny<Guid>(), It.IsAny<DateTime?>(), It.IsAny<DateTime?>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(new IInvoiceService.CloseBillingPeriodResult(IInvoiceService.CloseBillingPeriodOutcome.Closed, new InvoiceResponse { Id = Guid.NewGuid()}));
        var ctrl = WithUser(new InvoiceController(inv.Object), user);
        return (ctrl, inv);
    }

    // HrMember tất toán hóa đơn → 403; service KHÔNG được gọi.
    [Fact]
    public async Task PayInvoice_HrMember_tra_403_khong_goi_service()
    {
        var (ctrl, inv) = NewInvoiceController(HrMember());

        var result = await ctrl.PayInvoiceAsync(Guid.NewGuid());

        Assert.IsType<ForbidResult>(result.Result);
        inv.Verify(s => s.PayInvoiceAsync(It.IsAny<OwnerType>(), It.IsAny<Guid>(), It.IsAny<Guid>(),
            It.IsAny<CancellationToken>()), Times.Never);
    }

    // OrgAdmin tất toán hóa đơn → qua guard, tới logic (KHÔNG 403).
    [Fact]
    public async Task PayInvoice_OrgAdmin_khong_bi_chan()
    {
        var (ctrl, inv) = NewInvoiceController(OrgAdmin());

        var result = await ctrl.PayInvoiceAsync(Guid.NewGuid());

        Assert.IsNotType<ForbidResult>(result.Result);
        Assert.IsType<OkObjectResult>(result.Result);
    }

    // HrMember chốt kỳ → 403; service KHÔNG được gọi.
    [Fact]
    public async Task CloseBillingPeriod_HrMember_tra_403_khong_goi_service()
    {
        var (ctrl, inv) = NewInvoiceController(HrMember());

        var result = await ctrl.CloseBillingPeriodAsync(new CloseBillingPeriodRequest { OrgId = Guid.NewGuid() });

        Assert.IsType<ForbidResult>(result.Result);
        inv.Verify(s => s.CloseBillingPeriodAsync(It.IsAny<Guid>(), It.IsAny<DateTime?>(), It.IsAny<DateTime?>(),
            It.IsAny<CancellationToken>()), Times.Never);
    }

    // OrgAdmin chốt kỳ → qua guard, tới logic (KHÔNG 403).
    [Fact]
    public async Task CloseBillingPeriod_OrgAdmin_khong_bi_chan()
    {
        var (ctrl, inv) = NewInvoiceController(OrgAdmin());

        var result = await ctrl.CloseBillingPeriodAsync(new CloseBillingPeriodRequest { OrgId = Guid.NewGuid() });

        Assert.IsNotType<ForbidResult>(result.Result);
        Assert.IsType<OkObjectResult>(result.Result);
    }
}
