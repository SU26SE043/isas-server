using Isas.AuthService.DTOs;
using Isas.AuthService.Models;
using Microsoft.AspNetCore.Identity;
using Microsoft.EntityFrameworkCore;
using System.Security.Claims;

namespace Isas.AuthService.Services
{
    public class AuthService : IAuthService
    {
        private readonly AuthDbContext _authDbContext;
        private readonly IJwtService _jwtService;
        private readonly UserManager<User> _userManager;
        private readonly RoleManager<Role> _roleManager;
        private readonly IConfiguration _configuration;
        private readonly SignInManager<User> _signInManager;

        public AuthService(AuthDbContext authDbContext, IJwtService jwtService,
            UserManager<User> userManager, RoleManager<Role> roleManager,
            IConfiguration configuration, SignInManager<User> signInManager
            )
        {
            _authDbContext = authDbContext;
            _jwtService = jwtService;
            _userManager = userManager;
            _roleManager = roleManager;
            _configuration = configuration;
            _signInManager = signInManager;
        }

        public async Task<AuthResponse> LoginAsync(LoginRequest loginRequest)
        {
            var user = await _userManager.FindByEmailAsync(loginRequest.Email);
            if (user is null)
                throw new UnauthorizedAccessException("Invalid credentials");

            return await GenerateAuthResponse(user);
        }

        public async Task<AuthResponse> LoginGoogleAsync(ExternalLoginInfo info)
        {
            var result = await _signInManager.ExternalLoginSignInAsync(info.LoginProvider, info.ProviderKey, false);

            if (result.Succeeded)
            {
                var user = await _userManager.FindByLoginAsync(info.LoginProvider, info.ProviderKey);
                return await GenerateAuthResponse(user);
            }

            var email = info.Principal.FindFirstValue(ClaimTypes.Email);
            if (string.IsNullOrEmpty(email))
                throw new Exception("Google account does not provide an email");

            var newUser = new User
            {
                Id = Guid.NewGuid(),
                UserName = email,
                Email = email,
                FullName = info.Principal.Identity?.Name ?? email,
                CreatedAt = DateTime.UtcNow,
                UpdatedAt = DateTime.UtcNow
            };

            var identityResult = await _userManager.CreateAsync(newUser);
            if (!identityResult.Succeeded)
                throw new Exception(string.Join("; ", identityResult.Errors.Select(e => e.Description)));

            await _userManager.AddLoginAsync(newUser, info);
            await EnsureRoleExistsAsync("Candidate");
            await _userManager.AddToRoleAsync(newUser, "Candidate");

            return await GenerateAuthResponse(newUser);
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
                throw new UnauthorizedAccessException("Invalid refresh token");

            if (existingToken.ExpiresAt < DateTime.UtcNow)
                throw new UnauthorizedAccessException("Refresh token expired");

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
            var membership = await GetMembershipAsync(existingToken.UserId);
            var accessToken = _jwtService.GenerateAccessToken(existingToken.User, roles, membership);

            return BuildAuthResponse(accessToken, newRawRefreshToken);
        }

        public async Task<string> RegisterAsync(RegisterRequest registerRequest)
        {
            var user = new User
            {
                Id = Guid.NewGuid(),
                UserName = registerRequest.Email,
                Email = registerRequest.Email,
                FullName = registerRequest.FullName,
                CreatedAt = DateTime.UtcNow,
                UpdatedAt = DateTime.UtcNow
            };

            var result = await _userManager.CreateAsync(user, registerRequest.Password);
            if (!result.Succeeded)
                throw new Exception(string.Join("; ", result.Errors.Select(e => e.Description)));

            await EnsureRoleExistsAsync("Candidate");
            await _userManager.AddToRoleAsync(user, "Candidate");

            return "User ID: " + user.Id;
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

        // A2: 1 user thuộc ≤1 org ở phase 1 (1 org = 1 OrgAdmin) → lấy membership đầu tiên (null nếu không thuộc org)
        private Task<OrgMember?> GetMembershipAsync(Guid userId) =>
            _authDbContext.OrgMembers.AsNoTracking()
                .FirstOrDefaultAsync(m => m.UserId == userId);

        private async Task<AuthResponse> GenerateAuthResponse(User user)
        {
            var roles = await _userManager.GetRolesAsync(user);
            var membership = await GetMembershipAsync(user.Id);
            var accessToken = _jwtService.GenerateAccessToken(user, roles, membership);
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

        public async Task<UserResponse> GetUserAsync(Guid userId)
        {
            var user = await _authDbContext.Users.FindAsync(userId);
            if (user is null)
                throw new KeyNotFoundException("User not found");

            return new UserResponse
            {
                Id = user.Id.ToString(),
                FullName = user.FullName,
                Email = user.Email,
                Location = user.Location,
                Title = user.Title,
                CreatedAt = user.CreatedAt,
                Role = (await _userManager.GetRolesAsync(user)).FirstOrDefault() ?? "No role"
            };
        }

        public async Task<string> UpdateUserAsync(Guid userId, UpdateProfileRequest request)
        {
            var user = await _authDbContext.Users.FindAsync(userId);
            if (user is null)
                throw new KeyNotFoundException("User not found");

            user.FullName = request.FullName ?? user.FullName;
            user.Location = request.Location ?? user.Location;
            user.Title = request.Title ?? user.Title;
            user.UpdatedAt = DateTime.UtcNow;

            _authDbContext.Users.Update(user);
            await _authDbContext.SaveChangesAsync();
            return "Updated profile object";
        }

        public Task<RefreshToken> GetRefreshTokenAsync(string refreshToken)
        {
            var refreshTokenHash = _jwtService.HashRefreshToken(refreshToken);
            return _authDbContext.RefreshTokens
                .AsNoTracking()
                .FirstOrDefaultAsync(x => x.Token == refreshTokenHash);
        }
    }
}