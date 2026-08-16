using System.Security.Claims;
using Microsoft.AspNetCore.Authentication.Google;
using Microsoft.AspNetCore.Identity;

namespace Isas.AuthService.Services
{
    /// <summary>
    /// Biến một Google ID token <b>đã verify</b> (đường native/mobile) thành <see cref="ExternalLoginInfo"/>
    /// — đúng thứ mà <see cref="IAuthService.LoginGoogleAsync"/> nhận ở đường web.
    /// <para>
    /// Vì sao có lớp này thay vì viết luồng đăng nhập thứ hai: bên web,
    /// <c>SignInManager.GetExternalLoginInfoAsync()</c> làm đúng phép biến đổi này từ cookie do handler
    /// OAuth ghi. Mobile không có cookie đó, nhưng có ID token mang cùng bộ claim. Dựng lại
    /// <c>ExternalLoginInfo</c> ở đây rồi gọi CHUNG một hàm nghiệp vụ ⇒ account-linking, tạo user +
    /// role, chặn ban (F20), ghi <c>LoginEvent</c> (FR18) chỉ tồn tại MỘT bản, không thể trôi lệch nhau.
    /// </para>
    /// </summary>
    public static class GoogleExternalLogin
    {
        /// <summary>
        /// Tên provider. Lấy từ <see cref="GoogleDefaults.AuthenticationScheme"/> chứ không gõ tay
        /// <c>"Google"</c>: đây PHẢI là đúng chuỗi handler OAuth dùng, xem ghi chú ở <see cref="Create"/>.
        /// </summary>
        public const string Provider = GoogleDefaults.AuthenticationScheme;

        /// <summary>
        /// 🔑 <b>Bất biến quan trọng nhất của cả tính năng:</b> <c>ProviderKey</c> = claim <c>sub</c>
        /// của Google, và <c>LoginProvider</c> = <see cref="Provider"/> — trùng KHÍT thứ đường web sinh
        /// ra (handler Google map <c>sub</c> → <see cref="ClaimTypes.NameIdentifier"/>, rồi
        /// <c>GetExternalLoginInfoAsync</c> lấy claim đó làm <c>ProviderKey</c>).
        /// <para>
        /// Khớp thì người đăng nhập web hôm nay, mai đăng nhập app sẽ vào ĐÚNG account cũ. Lệch (ví dụ
        /// dùng email làm khoá) thì <c>FindByLoginAsync</c> tra không ra liên kết, rơi xuống nhánh
        /// "email đã tồn tại" và gắn thêm một external login thứ hai cho cùng một người — hỏng dần theo
        /// thời gian mà <b>không có lỗi nào nổ</b>. Có test khoá: đăng nhập web rồi mobile phải ra cùng user.
        /// </para>
        /// </summary>
        /// <exception cref="InvalidGoogleIdTokenException">
        /// Thiếu <c>sub</c>/email, hoặc email CHƯA XÁC MINH.
        /// </exception>
        public static ExternalLoginInfo Create(GoogleIdTokenPayload payload)
        {
            ArgumentNullException.ThrowIfNull(payload);

            if (string.IsNullOrWhiteSpace(payload.Subject))
                throw new InvalidGoogleIdTokenException("Google ID token thiếu 'sub'");

            if (string.IsNullOrWhiteSpace(payload.Email))
                throw new InvalidGoogleIdTokenException("Google ID token không kèm email");

            // 🔴 KHÔNG được bỏ: LoginGoogleAsync gắn external login vào account MẬT KHẨU sẵn có khi
            // trùng email. Đường web an toàn vì Google chỉ phát cookie đăng nhập cho account thật của
            // người dùng; đường này nhận token do CLIENT gửi lên, nên nếu chấp nhận email chưa xác minh
            // thì một account Google đăng ký bằng địa chỉ của người khác sẽ CHIẾM được account ISAS đó.
            if (!payload.EmailVerified)
                throw new InvalidGoogleIdTokenException("Email của account Google chưa được xác minh");

            var identity = new ClaimsIdentity(
                [
                    new Claim(ClaimTypes.NameIdentifier, payload.Subject),
                    new Claim(ClaimTypes.Email, payload.Email),
                    // LoginGoogleAsync đọc info.Principal.Identity?.Name để đặt FullName cho user mới;
                    // ClaimsIdentity.Name đọc đúng claim type này.
                    new Claim(ClaimTypes.Name, string.IsNullOrWhiteSpace(payload.Name) ? payload.Email : payload.Name)
                ],
                Provider);

            return new ExternalLoginInfo(new ClaimsPrincipal(identity), Provider, payload.Subject, Provider);
        }
    }
}
