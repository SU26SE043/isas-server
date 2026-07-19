using System.Reflection;
using Isas.PaymentService.Controllers;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;

namespace Isas.PaymentService.Tests;

/// <summary>
/// A5 (AUTH-3/6/7) — guard tĩnh: KHÔNG endpoint nào "trần". Mỗi action công khai của controller PHẢI được
/// phủ bởi <c>[Authorize]</c> (class/method) HOẶC <c>[AllowAnonymous]</c> (class/method) — bắt lỗi "quên bảo
/// vệ" ở tầng unit, không cần dựng host. + assert riêng: mutation gói + chốt kỳ hóa đơn = Roles="Admin";
/// catalog gói = public (payment.md:104).
/// </summary>
public class AuthorizationCoverageTests
{
    private static readonly Assembly ServiceAssembly = typeof(OrderController).Assembly;

    private static IEnumerable<Type> ControllerTypes() =>
        ServiceAssembly.GetTypes()
            .Where(t => t.IsPublic && !t.IsAbstract && typeof(ControllerBase).IsAssignableFrom(t));

    // Action = public, khai báo trực tiếp trên type, instance, không special-name, không [NonAction],
    // không phải override của member framework (ControllerBase/Controller).
    private static IEnumerable<MethodInfo> ActionMethods(Type controller) =>
        controller.GetMethods(BindingFlags.Public | BindingFlags.Instance | BindingFlags.DeclaredOnly)
            .Where(m => !m.IsSpecialName
                        && m.GetBaseDefinition().DeclaringType == m.DeclaringType
                        && m.GetCustomAttribute<NonActionAttribute>() is null);

    private static bool HasClassAuthz(Type t) =>
        t.GetCustomAttributes<AuthorizeAttribute>(inherit: true).Any()
        || t.GetCustomAttributes<AllowAnonymousAttribute>(inherit: true).Any();

    private static bool HasMethodAuthz(MethodInfo m) =>
        m.GetCustomAttributes<AuthorizeAttribute>(inherit: true).Any()
        || m.GetCustomAttributes<AllowAnonymousAttribute>(inherit: true).Any();

    [Fact]
    public void EveryAction_IsCoveredBy_AuthorizeOrAllowAnonymous()
    {
        var naked = new List<string>();
        foreach (var c in ControllerTypes())
        {
            var classCovered = HasClassAuthz(c);
            foreach (var a in ActionMethods(c))
                if (!classCovered && !HasMethodAuthz(a))
                    naked.Add($"{c.Name}.{a.Name}");
        }

        Assert.True(naked.Count == 0,
            "Action 'trần' (thiếu cả [Authorize] lẫn [AllowAnonymous]): " + string.Join(", ", naked));
    }

    [Theory]
    [InlineData(nameof(PackageController.CreatePackageAsync))]
    [InlineData(nameof(PackageController.UpdatePackageAsync))]
    [InlineData(nameof(PackageController.DeletePackageAsync))]
    public void PackageMutations_RequireAdminRole(string method)
    {
        var attr = typeof(PackageController).GetMethod(method)!.GetCustomAttribute<AuthorizeAttribute>();
        Assert.NotNull(attr);
        Assert.Equal("Admin", attr!.Roles);
    }

    [Theory]
    [InlineData(nameof(PackageController.GetAllPackageAsync))]
    [InlineData(nameof(PackageController.GetPackageAsync))]
    public void PackageCatalog_IsPublic(string method)
    {
        var m = typeof(PackageController).GetMethod(method)!;
        Assert.NotNull(m.GetCustomAttribute<AllowAnonymousAttribute>());
    }

    [Fact]
    public void AdminInvoiceClose_RequiresAdminRole()
    {
        var attr = typeof(InvoiceController).GetMethod(nameof(InvoiceController.CloseBillingPeriodAsync))!
            .GetCustomAttribute<AuthorizeAttribute>();
        Assert.NotNull(attr);
        Assert.Equal("Admin", attr!.Roles);
    }

    // F18/F19 — hai bề mặt admin MỚI. Hoàn tiền là mutation tiền, báo cáo doanh thu là dữ liệu tài chính
    // toàn hệ thống: cả hai phải Admin-only, không được rơi xuống `[Authorize]` trần (ai đăng nhập cũng vào).
    [Fact]
    public void AdminRefundVaRevenue_RequireAdminRole()
    {
        foreach (var t in new[]
                 {
                     typeof(AdminOrdersController),
                     typeof(AdminRevenueController),
                     // F20 — nguy hiểm nhất nhóm: nhận ownerId từ CLIENT (không phải từ token) và CỘNG
                     // credit. Rơi xuống [Authorize] trần = ai đăng nhập cũng tự cấp credit cho mình.
                     typeof(AdminCreditsController),
                 })
        {
            var attr = t.GetCustomAttributes<AuthorizeAttribute>(inherit: true).SingleOrDefault();
            Assert.NotNull(attr);
            Assert.Equal("Admin", attr!.Roles);
        }
    }

    // F19 — sổ cái credit của chủ ví: chỉ cần đăng nhập (chủ ví suy từ JWT nên không có đường đọc ví
    // người khác), nhưng KHÔNG được [AllowAnonymous] — đó là dữ liệu tài chính cá nhân.
    [Fact]
    public void SoCaiCuaChuVi_YeuCauDangNhap_KhongPhaiAnonymous()
    {
        var m = typeof(CreditAccountController)
            .GetMethod(nameof(CreditAccountController.GetMyCreditTransactionsAsync))!;

        Assert.NotNull(m.GetCustomAttribute<AuthorizeAttribute>());
        Assert.Null(m.GetCustomAttribute<AllowAnonymousAttribute>());
    }
}
