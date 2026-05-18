using Microsoft.AspNetCore.Identity;

namespace Isas.AuthService.Models
{
    public class Role : IdentityRole<Guid>
    {
        public ICollection<UserRole> UserRoles { get; set; } = new List<UserRole>();

        public ICollection<RoleClaim> RoleClaims { get; set; } = new List<RoleClaim>();
    }
}
