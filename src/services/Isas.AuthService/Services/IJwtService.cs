using Isas.AuthService.Models;

namespace Isas.AuthService.Services
{
    public interface IJwtService
    {
        string GenerateAccessToken(User user, IList<string> roles, OrgMember? membership = null);

        string GenerateRefreshToken();

        string HashRefreshToken(string refreshToken);
    }
}
