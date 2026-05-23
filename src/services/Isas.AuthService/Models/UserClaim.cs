using Microsoft.AspNetCore.Identity;

namespace Isas.AuthService.Models
{
    public class UserClaim : IdentityUserClaim<Guid>
    {
        public User User { get; set; } = default!;
    }
}
