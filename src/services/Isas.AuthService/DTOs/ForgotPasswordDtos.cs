using System.ComponentModel.DataAnnotations;

namespace Isas.AuthService.DTOs
{
    public class ForgotPasswordDtos
    {
        public class ForgotPasswordDto
        {
            public string Email { get; set; }
        }

        public class VerifyOtpDto
        {
            public string Email { get; set; }
            public string Otp { get; set; }
        }

        public class ResetPasswordDto
        {
            public string Email { get; set; }

            /// <summary>
            /// OTP người dùng vừa nhập — BẮT BUỘC gửi lại ở bước reset (không chỉ ở verify-otp).
            /// Cờ "đã verify" chỉ khoá theo email nên tự nó không chứng minh người gọi cầm OTP;
            /// bắt gửi kèm để bước đổi mật khẩu cũng phải xuất trình đúng bí mật đó.
            /// </summary>
            [Required]
            public string Otp { get; set; }

            public string NewPassword { get; set; }
        }
    }
}
