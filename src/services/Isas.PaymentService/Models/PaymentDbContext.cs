using Microsoft.EntityFrameworkCore;

namespace PaymentService.Models
{
    public class PaymentDbContext : DbContext
    {
        public PaymentDbContext(DbContextOptions<PaymentDbContext> options) : base(options) { }

        public DbSet<ProductPackage> ProductPackages => Set<ProductPackage>();
        public DbSet<Order> Orders => Set<Order>();
        public DbSet<PaymentTransaction> PaymentTransactions => Set<PaymentTransaction>();
        public DbSet<Subscription> Subscriptions => Set<Subscription>();
        public DbSet<CreditAccount> CreditAccounts => Set<CreditAccount>();
        public DbSet<CreditReservation> CreditReservations => Set<CreditReservation>();
        public DbSet<CreditTransaction> CreditTransactions => Set<CreditTransaction>();

        protected override void OnModelCreating(ModelBuilder modelBuilder)
        {
            // ── ProductPackage ────────────────────────────────────
            modelBuilder.Entity<ProductPackage>(e =>
            {
                e.HasKey(x => x.Id);
                e.Property(x => x.Id).HasDefaultValueSql("gen_random_uuid()");
                e.Property(x => x.Name).IsRequired();
                e.Property(x => x.Type).IsRequired();
                e.Property(x => x.PriceVnd).IsRequired();
                e.Property(x => x.IsActive).HasDefaultValue(true);
                e.Property(x => x.CreatedAt).HasDefaultValueSql("now()");
            });

            // ── Order ─────────────────────────────────────────────
            modelBuilder.Entity<Order>(e =>
            {
                e.HasKey(x => x.Id);
                e.Property(x => x.Id).HasDefaultValueSql("gen_random_uuid()");
                // Pre-existing bug (features/payment-b2c): default "pending" (string) trên property enum
                // OrderStatus không có conversion → EF ném InvalidOperationException lúc build model
                // (chặn luôn DbContext, kể cả 3 bảng credit mới cùng context). Fix tối thiểu: thêm
                // HasConversion<string> (đúng luôn GEN-2 enum-lưu-string) — KHÔNG đổi field/behavior Order.
                e.Property(x => x.Status).HasConversion<string>().HasDefaultValue(OrderStatus.Pending);
                e.Property(x => x.AmountVnd).IsRequired();
                e.HasIndex(x => x.PayosOrderCode).IsUnique();
                e.Property(x => x.CreatedAt).HasDefaultValueSql("now()");

                e.HasOne(x => x.Package)
                 .WithMany(x => x.Orders)
                 .HasForeignKey(x => x.PackageId)
                 .OnDelete(DeleteBehavior.Restrict);
            });

            // ── PaymentTransaction ────────────────────────────────
            modelBuilder.Entity<PaymentTransaction>(e =>
            {
                e.HasKey(x => x.Id);
                e.Property(x => x.Id).HasDefaultValueSql("gen_random_uuid()");
                e.Property(x => x.Gateway).HasDefaultValue("payos");
                e.Property(x => x.Status).IsRequired();
                e.Property(x => x.CreatedAt).HasDefaultValueSql("now()");

                e.HasOne(x => x.Order)
                 .WithOne(x => x.PaymentTransaction)
                 .HasForeignKey<PaymentTransaction>(x => x.OrderId)
                 .OnDelete(DeleteBehavior.Restrict);
            });

            // ── Subscription ──────────────────────────────────────
            modelBuilder.Entity<Subscription>(e =>
            {
                e.HasKey(x => x.Id);
                e.Property(x => x.Id).HasDefaultValueSql("gen_random_uuid()");
                e.Property(x => x.Status).HasDefaultValue("active");
                e.Property(x => x.CreatedAt).HasDefaultValueSql("now()");

                e.HasOne(x => x.Order)
                 .WithOne(x => x.Subscription)
                 .HasForeignKey<Subscription>(x => x.OrderId)
                 .OnDelete(DeleteBehavior.Restrict);

                e.HasOne(x => x.Package)
                 .WithMany(x => x.Subscriptions)
                 .HasForeignKey(x => x.PackageId)
                 .OnDelete(DeleteBehavior.Restrict);
            });

            // ── CreditAccount (P1 — ví của chủ sở hữu, D15) ─────────
            modelBuilder.Entity<CreditAccount>(e =>
            {
                e.ToTable("credit_accounts");
                e.HasKey(x => x.Id);
                e.Property(x => x.Id).HasDefaultValueSql("gen_random_uuid()");

                e.Property(x => x.OwnerType).HasConversion<string>().HasMaxLength(8).IsRequired();
                e.Property(x => x.OwnerId).IsRequired();

                e.Property(x => x.PaymentMode).HasConversion<string>().HasMaxLength(16)
                 .HasDefaultValue(PaymentMode.Prepaid);
                e.Property(x => x.Status).HasConversion<string>().HasMaxLength(16)
                 .HasDefaultValue(CreditAccountStatus.Active);

                e.Property(x => x.RemainingCredits).HasDefaultValue(0);
                e.Property(x => x.ReservedCredits).HasDefaultValue(0);

                e.Property(x => x.UpdatedAt).HasDefaultValueSql("now()");

                // 1 ví / (owner_type, owner_id) — §DB payment.md
                e.HasIndex(x => new { x.OwnerType, x.OwnerId }).IsUnique();
            });

            // ── CreditReservation (P1 — schema only; Reserve/Consume/Release = P4/P5/P6) ─
            modelBuilder.Entity<CreditReservation>(e =>
            {
                e.ToTable("credit_reservations");
                e.HasKey(x => x.Id);
                e.Property(x => x.Id).HasDefaultValueSql("gen_random_uuid()");

                e.Property(x => x.OwnerType).HasConversion<string>().HasMaxLength(8).IsRequired();
                e.Property(x => x.OwnerId).IsRequired();
                e.Property(x => x.SessionId).IsRequired();

                e.Property(x => x.Status).HasConversion<string>().HasMaxLength(16)
                 .HasDefaultValue(ReservationStatus.Reserved);

                e.Property(x => x.CreatedAt).HasDefaultValueSql("now()");

                // idempotency: 1 reservation / session (D7)
                e.HasIndex(x => x.SessionId).IsUnique();
            });

            // ── CreditTransaction (P1 — sổ cái, refactor owner_type) ─
            modelBuilder.Entity<CreditTransaction>(e =>
            {
                e.ToTable("credit_transactions");
                e.HasKey(x => x.Id);
                e.Property(x => x.Id).HasDefaultValueSql("gen_random_uuid()");

                e.Property(x => x.OwnerType).HasConversion<string>().HasMaxLength(8).IsRequired();
                e.Property(x => x.OwnerId).IsRequired();

                e.Property(x => x.Reason).HasConversion<string>().HasMaxLength(16).IsRequired();
                e.Property(x => x.CreatedAt).HasDefaultValueSql("now()");

                e.HasOne(x => x.Order)
                 .WithMany(x => x.CreditTransactions)
                 .HasForeignKey(x => x.OrderId)
                 .IsRequired(false)
                 .OnDelete(DeleteBehavior.SetNull);
            });
        }
    }
}
