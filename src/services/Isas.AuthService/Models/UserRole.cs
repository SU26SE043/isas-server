using Microsoft.AspNetCore.Identity;

namespace Isas.AuthService.Models
{
    public class UserRole : IdentityUserRole<Guid>
    {
        public User User { get; set; } = default!;

        public Role Role { get; set; } = default!;
    }
}
