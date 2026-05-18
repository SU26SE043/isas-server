using Microsoft.EntityFrameworkCore;

namespace Isas.AuthService.Models;

public class AuthDbContext : DbContext
{
    public AuthDbContext(DbContextOptions<AuthDbContext> options)
        : base(options)
    {
    }

    public DbSet<User> Users => Set<User>();
    public DbSet<Role> Roles => Set<Role>();
    public DbSet<UserRole> UserRoles => Set<UserRole>();
    public DbSet<UserClaim> UserClaims => Set<UserClaim>();
    public DbSet<RoleClaim> RoleClaims => Set<RoleClaim>();
    public DbSet<UserToken> UserTokens => Set<UserToken>();
    public DbSet<RefreshToken> RefreshTokens => Set<RefreshToken>();

    protected override void OnModelCreating(ModelBuilder builder)
    {
        base.OnModelCreating(builder);

        // ================= USERS =================

        builder.Entity<User>(e =>
        {
            e.ToTable("users");

            e.HasKey(x => x.Id);

            e.Property(x => x.Id)
                .HasColumnName("id");

            e.Property(x => x.UserName)
                .HasColumnName("user_name");

            e.Property(x => x.NormalizedUserName)
                .HasColumnName("normalized_user_name");

            e.Property(x => x.Email)
                .HasColumnName("email");

            e.Property(x => x.NormalizedEmail)
                .HasColumnName("normalized_email");

            e.Property(x => x.EmailConfirmed)
                .HasColumnName("email_confirmed");

            e.Property(x => x.PasswordHash)
                .HasColumnName("password_hash");

            e.Property(x => x.SecurityStamp)
                .HasColumnName("security_stamp");

            e.Property(x => x.ConcurrencyStamp)
                .HasColumnName("concurrency_stamp");

            e.Property(x => x.LockoutEnd)
                .HasColumnName("lockout_end");

            e.Property(x => x.LockoutEnabled)
                .HasColumnName("lockout_enabled");

            e.Property(x => x.AccessFailedCount)
                .HasColumnName("access_failed_count");

            e.Property(x => x.FullName)
                .HasColumnName("full_name");

            e.Property(x => x.Location)
                .HasColumnName("location");

            e.Property(x => x.Title)
                .HasColumnName("title");

            e.Property(x => x.CreatedAt)
                .HasColumnName("created_at");

            e.Property(x => x.UpdatedAt)
                .HasColumnName("updated_at");

            builder.Entity<User>().Ignore(x => x.PhoneNumber);

            builder.Entity<User>().Ignore(x => x.PhoneNumberConfirmed);

            builder.Entity<User>().Ignore(x => x.TwoFactorEnabled);
        });

        // ================= ROLES =================

        builder.Entity<Role>(e =>
        {
            e.ToTable("roles");

            e.HasKey(x => x.Id);

            e.Property(x => x.Id)
                .HasColumnName("id");

            e.Property(x => x.Name)
                .HasColumnName("name");

            e.Property(x => x.NormalizedName)
                .HasColumnName("normalized_name");

            e.Property(x => x.ConcurrencyStamp)
                .HasColumnName("concurrency_stamp");
        });

        // ================= USER ROLES =================

        builder.Entity<UserRole>(e =>
        {
            e.ToTable("user_roles");

            e.HasKey(x => new { x.UserId, x.RoleId });

            e.Property(x => x.UserId)
                .HasColumnName("user_id");

            e.Property(x => x.RoleId)
                .HasColumnName("role_id");

            e.HasOne(x => x.User)
                .WithMany(x => x.UserRoles)
                .HasForeignKey(x => x.UserId);

            e.HasOne(x => x.Role)
                .WithMany(x => x.UserRoles)
                .HasForeignKey(x => x.RoleId);
        });

        // ================= USER CLAIMS =================

        builder.Entity<UserClaim>(e =>
        {
            e.ToTable("user_claims");

            e.HasKey(x => x.Id);

            e.Property(x => x.Id)
                .HasColumnName("id");

            e.Property(x => x.UserId)
                .HasColumnName("user_id");

            e.Property(x => x.ClaimType)
                .HasColumnName("claim_type");

            e.Property(x => x.ClaimValue)
                .HasColumnName("claim_value");
        });

        // ================= ROLE CLAIMS =================

        builder.Entity<RoleClaim>(e =>
        {
            e.ToTable("role_claims");

            e.HasKey(x => x.Id);

            e.Property(x => x.Id)
                .HasColumnName("id");

            e.Property(x => x.RoleId)
                .HasColumnName("role_id");

            e.Property(x => x.ClaimType)
                .HasColumnName("claim_type");

            e.Property(x => x.ClaimValue)
                .HasColumnName("claim_value");
        });

        // ================= USER TOKENS =================

        builder.Entity<UserToken>(e =>
        {
            e.ToTable("user_tokens");

            e.HasKey(x => new
            {
                x.UserId,
                x.LoginProvider,
                x.Name
            });

            e.Property(x => x.UserId)
                .HasColumnName("user_id");

            e.Property(x => x.LoginProvider)
                .HasColumnName("login_provider");

            e.Property(x => x.Name)
                .HasColumnName("name");

            e.Property(x => x.Value)
                .HasColumnName("value");
        });

        // ================= REFRESH TOKENS =================

        builder.Entity<RefreshToken>(e =>
        {
            e.ToTable("refresh_tokens");

            e.HasKey(x => x.Id);

            e.Property(x => x.Id)
                .HasColumnName("id");

            e.Property(x => x.UserId)
                .HasColumnName("user_id");

            e.Property(x => x.Token)
                .HasColumnName("token");

            e.Property(x => x.IsRevoked)
                .HasColumnName("is_revoked");

            e.Property(x => x.ReplacedBy)
                .HasColumnName("replaced_by");

            e.Property(x => x.ExpiresAt)
                .HasColumnName("expires_at");

            e.Property(x => x.CreatedAt)
                .HasColumnName("created_at");

            e.HasOne(x => x.User)
                .WithMany(x => x.RefreshTokens)
                .HasForeignKey(x => x.UserId);
        });
    }
}