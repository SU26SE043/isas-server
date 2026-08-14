using System.Reflection;
using Isas.AuthService.Controllers;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;

namespace Isas.AuthService.Tests;

/// <summary>
/// A5 (AUTH-3/6/7) — guard tĩnh: KHÔNG endpoint nào "trần". Mỗi action controller PHẢI có [Authorize]
/// (class/method) HOẶC [AllowAnonymous] (class/method). + assert: OrgMembers = Roles="Employer";
/// auth-entry (register/login/refresh/…) = [AllowAnonymous] tường minh.
/// </summary>
public class AuthorizationCoverageTests
{
    private static readonly Assembly ServiceAssembly = typeof(AuthController).Assembly;

    private static IEnumerable<Type> ControllerTypes() =>
        ServiceAssembly.GetTypes()
            .Where(t => t.IsPublic && !t.IsAbstract && typeof(ControllerBase).IsAssignableFrom(t));

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

    [Fact]
    public void OrgMembers_RequireEmployerRole()
    {
        var attr = typeof(OrgMembersController).GetCustomAttribute<AuthorizeAttribute>();
        Assert.NotNull(attr);
        Assert.Equal("Employer", attr!.Roles);
    }

    [Theory]
    [InlineData(nameof(AuthController.RegisterAsync))]
    [InlineData(nameof(AuthController.RegisterOrgAsync))]
    [InlineData(nameof(AuthController.Login))]
    [InlineData(nameof(AuthController.LoginWithGoogle))]
    [InlineData(nameof(AuthController.GoogleLoginCallback))]
    [InlineData(nameof(AuthController.LoginWithGoogleIdToken))]
    [InlineData(nameof(AuthController.RefreshTokenAsync))]
    [InlineData(nameof(AuthController.ForgotPassword))]
    [InlineData(nameof(AuthController.VerifyOtp))]
    [InlineData(nameof(AuthController.ResetPassword))]
    public void AuthEntryEndpoints_AreExplicitlyAnonymous(string method)
    {
        var m = typeof(AuthController).GetMethod(method)!;
        Assert.NotNull(m.GetCustomAttribute<AllowAnonymousAttribute>());
    }
}
