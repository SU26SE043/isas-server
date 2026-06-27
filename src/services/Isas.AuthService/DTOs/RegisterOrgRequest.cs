using System.ComponentModel.DataAnnotations;

namespace Isas.AuthService.DTOs
{
    public class RegisterOrgRequest
    {
        [Required]
        [EmailAddress]
        public string Email { get; set; } = null!;

        [Required]
        [MinLength(6, ErrorMessage = "Password must be at least 6 characters long.")]
        public string Password { get; set; } = null!;

        [Required]
        public string FullName { get; set; } = null!;

        [Required]
        public string OrgName { get; set; } = null!;

        public string? TaxCode { get; set; }
    }
}
