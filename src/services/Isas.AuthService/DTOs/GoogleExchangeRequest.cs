using System.ComponentModel.DataAnnotations;

namespace Isas.AuthService.DTOs
{
    /// <summary>
    /// Body của <c>POST /auth/google/exchange</c> — đổi mã dùng-một-lần (nhận qua redirect callback
    /// Google) lấy <see cref="AuthResponse"/>. Mã chỉ sống vài chục giây và chết sau lần đổi đầu.
    /// </summary>
    public class GoogleExchangeRequest
    {
        [Required]
        public string Code { get; set; } = string.Empty;
    }
}
