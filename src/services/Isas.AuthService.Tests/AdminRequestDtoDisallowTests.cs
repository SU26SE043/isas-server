using System.Reflection;
using System.Text.Json;
using System.Text.Json.Serialization;
using Isas.AuthService.Controllers;
using Isas.AuthService.DTOs;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;

namespace Isas.AuthService.Tests;

/// <summary>
/// B10 (2026-09-16) — guard tĩnh: MỌI DTO nhận body ở action ADMIN phải mang
/// <c>[JsonUnmappedMemberHandling(Disallow)]</c>.
///
/// <para><b>Vì sao:</b> System.Text.Json mặc định BỎ QUA khoá lạ. Với DTO admin điều đó biến "FE gửi sai tên
/// trường" thành "BE 200 và âm thầm không làm gì" — đúng cách màn rubric admin hỏng gần một tháng
/// (FE gửi <c>description</c>, BE đợi <c>descriptor</c> ⇒ <c>changed:false</c> ⇒ 200, admin tưởng đã lưu),
/// và cùng lớp với 5 sự cố lệch khoá đã ghi trong progress.md (<c>focusCriteria</c> · <c>metricsVersion</c>
/// · <c>adaptiveMaxQuestions</c> · <c>Seniority</c> · rubric). Với DTO admin, khoá lạ = client sai ⇒ 400
/// ngay là đúng; CHỈ áp cho DTO admin vì client duy nhất là FE nội bộ — DTO callback/internal/public giữ
/// mặc định tha thứ (nới hợp đồng theo từng bước deploy).</para>
///
/// <para>Guard này quét reflection để DTO admin THÊM SAU tự bị bắt — không phải liệt kê tay.</para>
/// </summary>
public class AdminRequestDtoDisallowTests
{
    private static readonly Assembly ServiceAssembly = typeof(AdminController).Assembly;

    /// <summary>
    /// "Admin" phải là role DUY NHẤT của [Authorize] — endpoint người dùng thường mở cho
    /// <c>"Candidate, Employer, Admin"</c> (đổi mật khẩu, hồ sơ, logout) KHÔNG phải admin-only và có client
    /// ngoài FE nội bộ (mobile), nên không được siết.
    /// </summary>
    private static bool IsAdminAuthorize(IEnumerable<AuthorizeAttribute> attrs) =>
        attrs.Any(a =>
        {
            var roles = (a.Roles ?? string.Empty).Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
            return roles.Length == 1 && roles[0] == "Admin";
        });

    private static bool IsBodyDto(ParameterInfo p)
    {
        var t = Nullable.GetUnderlyingType(p.ParameterType) ?? p.ParameterType;
        if (p.GetCustomAttribute<FromBodyAttribute>() is not null) return true;
        if (p.GetCustomAttribute<FromQueryAttribute>() is not null
            || p.GetCustomAttribute<FromRouteAttribute>() is not null
            || p.GetCustomAttribute<FromFormAttribute>() is not null
            || p.GetCustomAttribute<FromHeaderAttribute>() is not null
            || p.GetCustomAttribute<FromServicesAttribute>() is not null) return false;
        // [ApiController] suy ra [FromBody] cho kiểu phức tạp không phải kiểu hệ thống.
        return t.IsClass && t != typeof(string) && t.Assembly == ServiceAssembly;
    }

    /// <summary>Mọi (controller, action, DTO) admin nhận body — dữ liệu cho cả hai test dưới.</summary>
    public static IEnumerable<object[]> AdminBodyDtos()
    {
        foreach (var c in ServiceAssembly.GetTypes().Where(t => t.IsPublic && !t.IsAbstract && typeof(ControllerBase).IsAssignableFrom(t)))
        {
            var classAdmin = IsAdminAuthorize(c.GetCustomAttributes<AuthorizeAttribute>(inherit: true));
            foreach (var a in c.GetMethods(BindingFlags.Public | BindingFlags.Instance | BindingFlags.DeclaredOnly)
                         .Where(m => !m.IsSpecialName && m.GetCustomAttribute<NonActionAttribute>() is null))
            {
                var own = a.GetCustomAttributes<AuthorizeAttribute>(inherit: true).ToList();
                var isAdmin = own.Count > 0 ? IsAdminAuthorize(own)
                    : classAdmin && a.GetCustomAttribute<AllowAnonymousAttribute>() is null;
                if (!isAdmin) continue;
                foreach (var p in a.GetParameters().Where(IsBodyDto))
                    yield return [c.Name, a.Name, Nullable.GetUnderlyingType(p.ParameterType) ?? p.ParameterType];
            }
        }
    }

    [Fact]
    public void CoDtoAdminDeQuet_KhongPhaiGuardRong()
        => Assert.NotEmpty(AdminBodyDtos()); // guard mà quét ra 0 DTO là guard chết, không phải service sạch

    [Theory]
    [MemberData(nameof(AdminBodyDtos))]
    public void DtoAdminNhanBody_PhaiDisallowKhoaLa(string controller, string action, Type dto)
    {
        var attr = dto.GetCustomAttribute<JsonUnmappedMemberHandlingAttribute>(inherit: false);
        Assert.True(attr is not null && attr.UnmappedMemberHandling == JsonUnmappedMemberHandling.Disallow,
            $"{controller}.{action}: DTO {dto.Name} thiếu [JsonUnmappedMemberHandling(Disallow)] — khoá lạ sẽ bị nuốt im lặng (200 no-op).");
    }

    /// <summary>
    /// Hành vi thật của attribute (không chỉ sự có mặt): khoá lạ ⇒ <see cref="JsonException"/> — ASP.NET
    /// input formatter đổi nó thành 400 cho [ApiController]. Dùng đúng options Web (case-insensitive) như
    /// pipeline thật.
    /// </summary>
    [Fact]
    public void KhoaLa_NemJsonException_KhoaDung_VanParse()
    {
        var opts = new JsonSerializerOptions(JsonSerializerDefaults.Web);
        Assert.Throws<JsonException>(() => JsonSerializer.Deserialize<ChangePlatformRoleRequest>("{\"role\":\"Admin\",\"roles\":\"Admin\"}", opts));
        Assert.NotNull(JsonSerializer.Deserialize<ChangePlatformRoleRequest>("{\"role\":\"Admin\"}", opts));
    }
}
