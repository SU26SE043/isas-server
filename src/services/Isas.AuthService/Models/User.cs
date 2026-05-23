using Microsoft.AspNetCore.Identity;

namespace Isas.AuthService.Models
{
    public class User : IdentityUser<Guid>
    {
        public string? FullName { get; set; }

        public string? Location { get; set; }

        public string? Title { get; set; }

        public DateTime CreatedAt { get; set; } = DateTime.UtcNow;

        public DateTime? UpdatedAt { get; set; }

        public ICollection<UserRole> UserRoles { get; set; } = new List<UserRole>();

        public ICollection<UserClaim> UserClaims { get; set; } = new List<UserClaim>();

        public ICollection<UserToken> UserTokens { get; set; } = new List<UserToken>();

        public ICollection<RefreshToken> RefreshTokens { get; set; } = new List<RefreshToken>();
    }
}
