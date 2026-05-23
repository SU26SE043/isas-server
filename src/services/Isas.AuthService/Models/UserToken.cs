using Microsoft.AspNetCore.Identity;

namespace Isas.AuthService.Models
{
    public class UserToken : IdentityUserToken<Guid>
    {
        public User User { get; set; } = default!;
    }
}
