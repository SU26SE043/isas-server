using System.Security.Claims;
using Isas.PaymentService.Controllers;
using Isas.PaymentService.Services;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc;
using Moq;
using PaymentService.Models;
using static Isas.PaymentService.DTOs.OrderRequest;

namespace Isas.PaymentService.Tests;

/// <summary>
/// BK15 — owner-scope các endpoint order KHÔNG được lộ tồn tại đơn của chủ ví khác:
/// order-not-exist và other-owner PHẢI cùng trả <b>404</b> (không phân biệt được từ ngoài), thống nhất với
/// <c>GET /order/{id}/status</c> (P3, service trả null → 404) và các endpoint invoice owner-scope (P8b).
/// Trước BK15, <c>GET /order/{id}</c> và <c>DELETE /order/{id}</c> trả 403 cho ca other-owner → LỆCH → gom về 404.
/// Test ở tầng controller (biên đọc claim + map status code), kiểm OFFLINE bằng JWT claim (GEN-3).
/// </summary>
public class OrderOwnerScopeTests
{
    private static ClaimsPrincipal Principal(Guid userId)
    {
        var claims = new List<Claim> { new(ClaimTypes.NameIdentifier, userId.ToString()) };
        return new ClaimsPrincipal(new ClaimsIdentity(claims, "Test"));
    }

    private static T WithUser<T>(T controller, ClaimsPrincipal user) where T : ControllerBase
    {
        controller.ControllerContext = new ControllerContext
        {
            HttpContext = new DefaultHttpContext { User = user }
        };
        return controller;
    }

    private static OrderController NewController(Mock<IOrderService> order, ClaimsPrincipal user,
        Mock<IOrderStatusService>? status = null)
        => WithUser(new OrderController(order.Object, (status ?? new Mock<IOrderStatusService>()).Object), user);

    // ---------- GET /order/{id} ----------

    // Đơn của chủ ví khác → 404 (KHÔNG 403) — không lộ tồn tại đơn người khác.
    [Fact]
    public async Task GetOrder_other_owner_tra_404()
    {
        var caller = Guid.NewGuid();
        var other = Guid.NewGuid();
        var order = new Mock<IOrderService>();
        order.Setup(s => s.GetOrderAsync(It.IsAny<Guid>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(new OrderResponse { Id = Guid.NewGuid(), OwnerType = OwnerType.User, OwnerId = other });
        var ctrl = NewController(order, Principal(caller));

        var result = await ctrl.GetOrderAsync(Guid.NewGuid());

        Assert.IsType<NotFoundResult>(result.Result);
    }

    // Đơn không tồn tại → 404 (cùng mã với other-owner: không phân biệt được từ ngoài).
    [Fact]
    public async Task GetOrder_not_exist_tra_404()
    {
        var order = new Mock<IOrderService>();
        order.Setup(s => s.GetOrderAsync(It.IsAny<Guid>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync((OrderResponse?)null);
        var ctrl = NewController(order, Principal(Guid.NewGuid()));

        var result = await ctrl.GetOrderAsync(Guid.NewGuid());

        Assert.IsType<NotFoundResult>(result.Result);
    }

    // Đơn của chính chủ ví → 200 (đường thành công không bị BK15 chạm).
    [Fact]
    public async Task GetOrder_owner_tra_200()
    {
        var caller = Guid.NewGuid();
        var order = new Mock<IOrderService>();
        order.Setup(s => s.GetOrderAsync(It.IsAny<Guid>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(new OrderResponse { Id = Guid.NewGuid(), OwnerType = OwnerType.User, OwnerId = caller });
        var ctrl = NewController(order, Principal(caller));

        var result = await ctrl.GetOrderAsync(Guid.NewGuid());

        Assert.IsType<OkObjectResult>(result.Result);
    }

    // ---------- DELETE /order/{id} ----------

    // Cancel đơn của chủ ví khác → 404 (KHÔNG 403) — không lộ tồn tại đơn người khác.
    [Fact]
    public async Task CancelOrder_other_owner_tra_404_khong_goi_cancel()
    {
        var caller = Guid.NewGuid();
        var other = Guid.NewGuid();
        var order = new Mock<IOrderService>();
        order.Setup(s => s.GetOrderAsync(It.IsAny<Guid>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(new OrderResponse { Id = Guid.NewGuid(), OwnerType = OwnerType.User, OwnerId = other });
        var ctrl = NewController(order, Principal(caller));

        var result = await ctrl.CancelOrderAsync(Guid.NewGuid());

        Assert.IsType<NotFoundResult>(result);
        // Không được huỷ đơn người khác (dừng ở ownership check).
        order.Verify(s => s.CancelOrderAsync(It.IsAny<Guid>(), It.IsAny<CancellationToken>()), Times.Never);
    }

    // Cancel đơn không tồn tại → 404.
    [Fact]
    public async Task CancelOrder_not_exist_tra_404()
    {
        var order = new Mock<IOrderService>();
        order.Setup(s => s.GetOrderAsync(It.IsAny<Guid>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync((OrderResponse?)null);
        var ctrl = NewController(order, Principal(Guid.NewGuid()));

        var result = await ctrl.CancelOrderAsync(Guid.NewGuid());

        Assert.IsType<NotFoundResult>(result);
        order.Verify(s => s.CancelOrderAsync(It.IsAny<Guid>(), It.IsAny<CancellationToken>()), Times.Never);
    }

    // ---------- GET /order/{id}/status (P3 — giữ 404 cho other-owner/not-exist) ----------

    // Service owner-scope trả null (not-exist HOẶC other-owner) → controller 404 (khoá hợp đồng P3 giữ nguyên).
    [Fact]
    public async Task GetOrderStatus_null_tra_404()
    {
        var order = new Mock<IOrderService>();
        var status = new Mock<IOrderStatusService>();
        status.Setup(s => s.GetOrderStatusAsync(It.IsAny<Guid>(), It.IsAny<OwnerType>(), It.IsAny<Guid>(),
                It.IsAny<CancellationToken>()))
            .ReturnsAsync((OrderStatusResult?)null);
        var ctrl = NewController(order, Principal(Guid.NewGuid()), status);

        var result = await ctrl.GetOrderStatusAsync(Guid.NewGuid());

        Assert.IsType<NotFoundResult>(result.Result);
    }
}
