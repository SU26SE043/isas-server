using System.ComponentModel.DataAnnotations;

namespace Isas.AuthService.DTOs
{
    /// <summary>
    /// Body của <c>POST /auth/google/id-token</c> — đăng nhập Google <b>native</b> (mobile): app lấy
    /// ID token bằng Google Sign-In SDK của hệ điều hành rồi gửi thẳng lên, không qua vòng redirect
    /// trình duyệt như bản web.
    /// </summary>
    public class GoogleIdTokenRequest
    {
        /// <summary>
        /// ID token (JWT) do Google phát cho app. ⚠ App PHẢI xin token với <c>serverClientId</c> =
        /// <b>WEB</b> client ID, không phải Android/iOS client ID — <c>aud</c> của token phải nằm
        /// trong <c>Authentication:Google:IdTokenAudiences</c> thì server mới nhận.
        /// </summary>
        [Required]
        public string IdToken { get; set; } = string.Empty;
    }
}
