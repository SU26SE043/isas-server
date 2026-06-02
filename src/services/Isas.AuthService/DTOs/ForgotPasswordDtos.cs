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
            public string NewPassword { get; set; }
        }
    }
}
