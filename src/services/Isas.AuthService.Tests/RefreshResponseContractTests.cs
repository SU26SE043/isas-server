using Isas.AuthService.Controllers;
using Isas.AuthService.DTOs;
using Microsoft.AspNetCore.Mvc;

namespace Isas.AuthService.Tests;

/// <summary>
/// `POST /auth/refresh` phải KHAI đúng thứ nó thật sự trả về.
///
/// <para>Trước vòng này action khai <c>ActionResult&lt;RefreshTokenResponse&gt;</c> (chỉ có
/// <c>refreshToken</c> + <c>expiresAt</c>) trong khi <c>IAuthService.RefreshTokenAsync</c> trả
/// <c>AuthResponse</c>. JSON lúc chạy vẫn ĐÚNG — <c>ObjectResult</c> serialize theo kiểu thật — nên
/// không client nào gãy; cái gãy là <b>tài liệu</b>: OpenAPI/Scalar mô tả một response không có
/// <c>accessToken</c>. Ai sinh model từ đó (app mobile đang làm) sẽ đọc hụt access token, rồi refresh
/// vô tận mà không hiểu vì sao.</para>
///
/// <para>Khoá bằng kiểu khai của action chứ không bằng hình dạng JSON: chính KIỂU KHAI là thứ sinh ra
/// schema, và cũng chính là thứ đã sai.</para>
/// </summary>
public class RefreshResponseContractTests
{
    [Fact]
    public void Refresh_KhaiTraVeAuthResponse_CoAccessToken()
    {
        var method = typeof(AuthController).GetMethod(nameof(AuthController.RefreshTokenAsync));
        Assert.NotNull(method);

        Assert.Equal(typeof(Task<ActionResult<AuthResponse>>), method!.ReturnType);
    }

    // Đối chứng: test trên chỉ có nghĩa nếu AuthResponse thật sự mang accessToken. Đổi tên field bên
    // AuthResponse mà quên chỗ này thì hợp đồng vẫn "xanh" một cách vô nghĩa.
    [Fact]
    public void AuthResponse_CoDuBaField()
    {
        Assert.NotNull(typeof(AuthResponse).GetProperty(nameof(AuthResponse.AccessToken)));
        Assert.NotNull(typeof(AuthResponse).GetProperty(nameof(AuthResponse.RefreshToken)));
        Assert.NotNull(typeof(AuthResponse).GetProperty(nameof(AuthResponse.ExpiresAt)));
    }
}
