using System.ComponentModel.DataAnnotations;

namespace Isas.AuthService.DTOs
{
    public class RegisterRequest
    {
        [Required]
        public string UserName { get; set; }

        [Required]
        [EmailAddress]
        public string Email { get; set; }

        [Required]
        [MinLength(6, ErrorMessage = "Password must be at least 6 characters long.")]
        public string Password { get; set; }

        [Required]
        public string FullName { get; set; }

        [Required]
        public string Location { get; set; }

        [Required]
        public string Title { get; set; }
    }
}
