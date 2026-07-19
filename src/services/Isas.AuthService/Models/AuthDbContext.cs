using Microsoft.AspNetCore.Identity;
using Microsoft.AspNetCore.Identity.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore;

namespace Isas.AuthService.Models;

public class AuthDbContext : IdentityDbContext<User, Role, Guid, UserClaim, UserRole, IdentityUserLogin<Guid>, RoleClaim, UserToken>
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
    public DbSet<IdentityUserLogin<Guid>> UserLogins => Set<IdentityUserLogin<Guid>>();
    public DbSet<RefreshToken> RefreshTokens => Set<RefreshToken>();
    public DbSet<Organization> Organizations => Set<Organization>();
    public DbSet<OrgMember> OrgMembers => Set<OrgMember>();

    protected override void OnModelCreating(ModelBuilder builder)
    {
        base.OnModelCreating(builder);

        // ================= USERS =================

        builder.Entity<User>(e =>
        {
            e.ToTable("users");
            e.Property(x => x.Id).HasColumnName("id");
            e.Property(x => x.UserName).HasColumnName("user_name");
            e.Property(x => x.NormalizedUserName).HasColumnName("normalized_user_name");
            e.Property(x => x.Email).HasColumnName("email");
            e.Property(x => x.NormalizedEmail).HasColumnName("normalized_email");
            e.Property(x => x.EmailConfirmed).HasColumnName("email_confirmed");
            e.Property(x => x.PasswordHash).HasColumnName("password_hash");
            e.Property(x => x.SecurityStamp).HasColumnName("security_stamp");
            e.Property(x => x.ConcurrencyStamp).HasColumnName("concurrency_stamp");
            e.Property(x => x.LockoutEnd).HasColumnName("lockout_end");
            e.Property(x => x.LockoutEnabled).HasColumnName("lockout_enabled");
            e.Property(x => x.AccessFailedCount).HasColumnName("access_failed_count");
            e.Property(x => x.FullName).HasColumnName("full_name");
            e.Property(x => x.Location).HasColumnName("location");
            e.Property(x => x.Title).HasColumnName("title");
            e.Property(x => x.CreatedAt).HasColumnName("created_at");
            e.Property(x => x.UpdatedAt).HasColumnName("updated_at");
            // F20 — đình chỉ account do PlatformAdmin (tách khỏi lockout tự động của Identity).
            e.Property(x => x.BannedAt).HasColumnName("banned_at");
            e.Property(x => x.BanReason).HasColumnName("ban_reason").HasMaxLength(500);
            e.Property(x => x.BannedBy).HasColumnName("banned_by");
            e.Ignore(x => x.PhoneNumber);
            e.Ignore(x => x.PhoneNumberConfirmed);
            e.Ignore(x => x.TwoFactorEnabled);
            // DB11: email UNIQUE — override index EmailIndex mặc định của Identity (non-unique)
            // thành unique + filtered (chỉ áp cho email != null). Giữ tên "EmailIndex" để không
            // sinh index trùng; kết hợp options.User.RequireUniqueEmail=true (Program.cs) chặn dupe email.
            e.HasIndex(x => x.NormalizedEmail)
                .HasDatabaseName("EmailIndex")
                .IsUnique()
                .HasFilter("normalized_email IS NOT NULL");
        });

        // ================= ROLES =================
        builder.Entity<Role>(e =>
        {
            e.ToTable("roles");
            e.Property(x => x.Id).HasColumnName("id");
            e.Property(x => x.Name).HasColumnName("name");
            e.Property(x => x.NormalizedName).HasColumnName("normalized_name");
            e.Property(x => x.ConcurrencyStamp).HasColumnName("concurrency_stamp");
        });

        // ================= USER ROLES =================
        builder.Entity<UserRole>(e =>
        {
            e.ToTable("user_roles");
            e.Property(x => x.UserId).HasColumnName("user_id");
            e.Property(x => x.RoleId).HasColumnName("role_id");
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
            e.Property(x => x.Id).HasColumnName("id");
            e.Property(x => x.UserId).HasColumnName("user_id");
            e.Property(x => x.ClaimType).HasColumnName("claim_type");
            e.Property(x => x.ClaimValue).HasColumnName("claim_value");

            e.HasOne(x => x.User)
                .WithMany(x => x.UserClaims)
                .HasForeignKey(x => x.UserId);
        });

        // ================= ROLE CLAIMS =================
        builder.Entity<RoleClaim>(e =>
        {
            e.ToTable("role_claims");
            e.Property(x => x.Id).HasColumnName("id");
            e.Property(x => x.RoleId).HasColumnName("role_id");
            e.Property(x => x.ClaimType).HasColumnName("claim_type");
            e.Property(x => x.ClaimValue).HasColumnName("claim_value");

            e.HasOne(x => x.Role)
                .WithMany(x => x.RoleClaims)
                .HasForeignKey(x => x.RoleId);
        });

        // ================= USER TOKENS =================
        builder.Entity<UserToken>(e =>
        {
            e.ToTable("user_tokens");
            e.Property(x => x.UserId).HasColumnName("user_id");
            e.Property(x => x.LoginProvider).HasColumnName("login_provider");
            e.Property(x => x.Name).HasColumnName("name");
            e.Property(x => x.Value).HasColumnName("value");

            e.HasOne(x => x.User)
                .WithMany(x => x.UserTokens)
                .HasForeignKey(x => x.UserId);
        });

        // ================= USER LOGINS =================
        builder.Entity<IdentityUserLogin<Guid>>(e =>
        {
            e.ToTable("user_logins");
        });

        // ================= REFRESH TOKENS =================
        builder.Entity<RefreshToken>(e =>
        {
            e.ToTable("refresh_tokens");
            e.HasKey(x => x.Id);
            e.Property(x => x.Id).HasColumnName("id");
            e.Property(x => x.UserId).HasColumnName("user_id");
            e.Property(x => x.Token).HasColumnName("token");
            e.Property(x => x.IsRevoked).HasColumnName("is_revoked");
            e.Property(x => x.ReplacedBy).HasColumnName("replaced_by");
            e.Property(x => x.ExpiresAt).HasColumnName("expires_at");
            e.Property(x => x.CreatedAt).HasColumnName("created_at");
            // DB12: unique index trên token (đã là SHA-256 hash 32+ byte ngẫu nhiên → an toàn
            // unique). Vừa chống trùng token vừa biến lookup `x.Token == hash` (validate refresh)
            // từ full-scan thành index seek.
            e.HasIndex(x => x.Token).IsUnique();
            e.HasOne(x => x.User)
                .WithMany(x => x.RefreshTokens)
                .HasForeignKey(x => x.UserId);
        });

        // ================= ORGANIZATIONS =================
        builder.Entity<Organization>(e =>
        {
            e.ToTable("organizations");
            e.HasKey(x => x.Id);
            e.Property(x => x.Id).HasColumnName("id");
            e.Property(x => x.Name).HasColumnName("name").IsRequired();
            e.Property(x => x.TaxCode).HasColumnName("tax_code");
            e.Property(x => x.CreatedAt).HasColumnName("created_at");
        });

        // ================= ORG MEMBERS =================
        builder.Entity<OrgMember>(e =>
        {
            e.ToTable("org_members");
            e.HasKey(x => new { x.OrgId, x.UserId });
            e.Property(x => x.OrgId).HasColumnName("org_id");
            e.Property(x => x.UserId).HasColumnName("user_id");
            e.Property(x => x.OrgRole).HasColumnName("org_role")
                .HasMaxLength(16)
                .HasConversion<string>();
            e.Property(x => x.JoinedAt).HasColumnName("joined_at");

            e.HasOne(x => x.Organization)
                .WithMany(x => x.Members)
                .HasForeignKey(x => x.OrgId);
            e.HasOne(x => x.User)
                .WithMany(x => x.OrgMembers)
                .HasForeignKey(x => x.UserId);
        });

        // ================= DB10 — OPTIMISTIC CONCURRENCY (xmin) =================
        // Dùng cột hệ thống Postgres `xmin` làm concurrency token cho organizations + org_members: hai
        // admin sửa cùng org / cùng thành viên song song → lần ghi thứ hai bắt DbUpdateConcurrencyException
        // thay vì lost-update thầm lặng. Map tường minh property ẩn `xmin` (uint) → cột hệ thống `xid`
        // theo doc Npgsql (efcore.pg 10 đã bỏ helper UseXminAsConcurrencyToken()). `xmin` là system column
        // nên migration KHÔNG phát AddColumn/DropColumn (model-snapshot-only). GATE IsNpgsql: `xmin` KHÔNG
        // có tương đương SQLite → nhánh này bị bỏ qua dưới provider SQLite (EnsureCreated test) nên giữ
        // nguyên hành vi. (Introduce idiom IsNpgsql ở Auth — chưa có tiền lệ.)
        if (Database.IsNpgsql())
        {
            builder.Entity<Organization>().Property<uint>("xmin")
                .HasColumnName("xmin").HasColumnType("xid")
                .ValueGeneratedOnAddOrUpdate().IsConcurrencyToken();
            builder.Entity<OrgMember>().Property<uint>("xmin")
                .HasColumnName("xmin").HasColumnType("xid")
                .ValueGeneratedOnAddOrUpdate().IsConcurrencyToken();
        }
    }
}