using System.ComponentModel.DataAnnotations;

namespace Isas.AuthService.DTOs
{
    public class LoginRequest
    {
        [Required]
        public string Email { get; set; }
        [Required]
        public string Password { get; set; }
    }
}
