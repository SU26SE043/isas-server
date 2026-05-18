using Isas.AuthService.DTOs;
using Isas.AuthService.Models;
using Microsoft.AspNetCore.Identity;
using Microsoft.EntityFrameworkCore;

namespace Isas.AuthService.Services
{
    public class AuthService : IAuthService
    {
        private readonly AuthDbContext _authDbContext;
        private readonly IJwtService _jwtService;
        private readonly UserManager<User> _userManager;
        private readonly RoleManager<Role> _roleManager;
        private readonly IConfiguration _configuration;

        public AuthService(AuthDbContext authDbContext, IJwtService jwtService, UserManager<User> userManager, RoleManager<Role> roleManager, IConfiguration configuration)
        {
            _authDbContext = authDbContext;
            _jwtService = jwtService;
            _userManager = userManager;
            _roleManager = roleManager;
            _configuration = configuration;
        }

        public async Task<AuthResponse> LoginAsync(LoginRequest loginRequest)
        {
            var user = await _userManager.FindByEmailAsync(loginRequest.Email);

            if (user is null || !await _userManager.CheckPasswordAsync(user, loginRequest.Password))
            {
                throw new Exception("Invalid credentials");
            }

            return await GenerateAuthResponse(user);
        }

        public async Task LogoutAsync(string refreshToken)
        {
            var hashedToken = _jwtService.HashRefreshToken(refreshToken);

            var existingToken = await _authDbContext.RefreshTokens
                .FirstOrDefaultAsync(x => x.Token == hashedToken && !x.IsRevoked);

            if (existingToken is null) return;

            existingToken.IsRevoked = true;
            await _authDbContext.SaveChangesAsync();
        }

        public async Task<AuthResponse> RefreshTokenAsync(string refreshToken)
        {
            var hashedToken = _jwtService.HashRefreshToken(refreshToken);

            var existingToken = await _authDbContext.RefreshTokens
                .Include(x => x.User)
                .FirstOrDefaultAsync(x => x.Token == hashedToken && !x.IsRevoked);

            if (existingToken is null)
                throw new Exception("Invalid refresh token");

            if (existingToken.ExpiresAt < DateTime.UtcNow)
                throw new Exception("Refresh token expired");

            existingToken.IsRevoked = true;

            var newRawRefreshToken = _jwtService.GenerateRefreshToken();
            var newRefreshTokenEntity = new RefreshToken
            {
                Id = Guid.NewGuid(),
                UserId = existingToken.UserId,
                Token = _jwtService.HashRefreshToken(newRawRefreshToken),
                ExpiresAt = DateTime.UtcNow.AddDays(GetRefreshTokenDays()),
                CreatedAt = DateTime.UtcNow
            };

            existingToken.ReplacedBy = newRefreshTokenEntity.Id;
            _authDbContext.RefreshTokens.Add(newRefreshTokenEntity);
            await _authDbContext.SaveChangesAsync();

            var roles = await _userManager.GetRolesAsync(existingToken.User);
            var accessToken = _jwtService.GenerateAccessToken(existingToken.User, roles);

            return BuildAuthResponse(accessToken, newRawRefreshToken);
        }

        public async Task<AuthResponse> RegisterAsync(RegisterRequest registerRequest)
        {
            var existingUser = await _userManager.FindByEmailAsync(registerRequest.Email);
            if (existingUser is not null)
                throw new Exception("Email already exists.");

            var user = new User
            {
                Id = Guid.NewGuid(),
                UserName = registerRequest.UserName,
                Email = registerRequest.Email,
                FullName = registerRequest.FullName,
                Location = registerRequest.Location,
                Title = registerRequest.Title,
                CreatedAt = DateTime.UtcNow
            };

            var result = await _userManager.CreateAsync(user, registerRequest.Password);
            if (!result.Succeeded)
                throw new Exception(string.Join(", ", result.Errors.Select(x => x.Description)));

            await EnsureRoleExistsAsync("Candidate");
            await _userManager.AddToRoleAsync(user, "Candidate");

            return await GenerateAuthResponse(user);
        }

        private async Task EnsureRoleExistsAsync(string roleName)
        {
            if (!await _roleManager.RoleExistsAsync(roleName))
            {
                await _roleManager.CreateAsync(new Role
                {
                    Id = Guid.NewGuid(),
                    Name = roleName,
                    NormalizedName = roleName.ToUpperInvariant()
                });
            }
        }

        private async Task<AuthResponse> GenerateAuthResponse(User user)
        {
            var roles = await _userManager.GetRolesAsync(user);
            var accessToken = _jwtService.GenerateAccessToken(user, roles);
            var rawRefreshToken = _jwtService.GenerateRefreshToken();

            var refreshTokenEntity = new RefreshToken
            {
                Id = Guid.NewGuid(),
                UserId = user.Id,
                Token = _jwtService.HashRefreshToken(rawRefreshToken),
                ExpiresAt = DateTime.UtcNow.AddDays(GetRefreshTokenDays()),
                CreatedAt = DateTime.UtcNow
            };

            _authDbContext.RefreshTokens.Add(refreshTokenEntity);
            await _authDbContext.SaveChangesAsync();

            return BuildAuthResponse(accessToken, rawRefreshToken);
        }

        private int GetRefreshTokenDays() =>
            int.Parse(_configuration["Jwt:RefreshTokenDays"]
                ?? throw new InvalidOperationException("Jwt:RefreshTokenDays is not configured"));

        private int GetAccessTokenMinutes() =>
            int.Parse(_configuration["Jwt:AccessTokenMinutes"]
                ?? throw new InvalidOperationException("Jwt:AccessTokenMinutes is not configured"));

        private AuthResponse BuildAuthResponse(string accessToken, string refreshToken) => new()
        {
            AccessToken = accessToken,
            RefreshToken = refreshToken,
            ExpiresAt = DateTime.UtcNow.AddMinutes(GetAccessTokenMinutes())
        };
    }
}