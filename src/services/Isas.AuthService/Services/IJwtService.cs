using Isas.AuthService.Models;

namespace Isas.AuthService.Services
{
    public interface IJwtService
    {
        string GenerateAccessToken(User user, IList<string> roles);

        string GenerateRefreshToken();

        string HashRefreshToken(string refreshToken);
    }
}
