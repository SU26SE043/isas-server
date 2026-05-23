using Microsoft.AspNetCore.Identity;

namespace Isas.AuthService.Models
{
    public class RoleClaim :IdentityRoleClaim<Guid>
    {
        public Role Role { get; set; } = default!;
    }
}
