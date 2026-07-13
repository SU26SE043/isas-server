using System.Reflection;
using System.Security.Claims;
using Isas.PaymentService.Controllers;
using Isas.PaymentService.Services;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Mvc.Routing;
using Moq;
using PaymentService.Models;
using static Isas.PaymentService.DTOs.OrderRequest;

namespace Isas.PaymentService.Tests;

/// <summary>
/// BF4 — POST /order trả 201 phải dùng route name tường minh. Mặc định ASP.NET strip 'Async' suffix
/// (SuppressAsyncSuffixInActionNames=true) → nameof(GetOrderAsync)='GetOrderAsync' KHÔNG khớp action
/// 'GetOrder' → CreatedAtAction ném "No route matches the supplied values" khi format 201 (bắt ở API
/// sweep layer-3 sau khi BF3 mở đường PayOS thật). Guard: create dùng CreatedAtRoute("GetOrderById")
/// + action GET mang Name="GetOrderById" (link generation thật chỉ chạy ở pipeline MVC = layer-3).
/// </summary>
public class OrderCreatedResultBf4Tests
{
    private static OrderController NewController(Mock<IOrderService> order, ClaimsPrincipal user)
    {
        var ctrl = new OrderController(order.Object, new Mock<IOrderStatusService>().Object)
        {
            ControllerContext = new ControllerContext
            {
                HttpContext = new DefaultHttpContext { User = user }
            }
        };
        return ctrl;
    }

    // B2C User (chỉ có NameIdentifier, không org_role) → qua guard IsHrMember/GetOwner.
    private static ClaimsPrincipal B2CUser(Guid userId) =>
        new(new ClaimsIdentity(new[] { new Claim(ClaimTypes.NameIdentifier, userId.ToString()) }, "Test"));

    [Fact]
    public async Task CreateOrder_returns_201_CreatedAtRoute_with_GetOrderById()
    {
        var caller = Guid.NewGuid();
        var created = new OrderResponse
        {
            Id = Guid.NewGuid(), OwnerType = OwnerType.User, OwnerId = caller,
            CheckoutUrl = "https://pay.payos.vn/web/xyz",
        };
        var order = new Mock<IOrderService>();
        order.Setup(s => s.CreateOrderAsync(OwnerType.User, caller, It.IsAny<CreateOrderRequest>(),
                It.IsAny<CancellationToken>()))
            .ReturnsAsync(created);
        var ctrl = NewController(order, B2CUser(caller));

        var result = await ctrl.CreateOrderAsync(new CreateOrderRequest { PackageId = Guid.NewGuid() });

        var createdAt = Assert.IsType<CreatedAtRouteResult>(result.Result);
        Assert.Equal("GetOrderById", createdAt.RouteName);        // BF4 — không phải nameof(GetOrderAsync)
        Assert.Equal(created.Id, createdAt.RouteValues!["id"]);
        Assert.Same(created, createdAt.Value);                     // body mang CheckoutUrl cho FE
    }

    [Fact]
    public void GetOrder_action_defines_the_GetOrderById_route_name()
    {
        // Đầu kia của khớp nối: action GET phải MANG route name mà CreatedAtRoute trỏ tới.
        var method = typeof(OrderController).GetMethod("GetOrderAsync",
            BindingFlags.Public | BindingFlags.Instance)!;
        var httpGet = method.GetCustomAttribute<HttpGetAttribute>()!;

        Assert.NotNull(httpGet);
        Assert.Equal("GetOrderById", httpGet.Name);
    }
}
