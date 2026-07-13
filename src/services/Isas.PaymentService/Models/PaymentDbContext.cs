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
        public DbSet<Invoice> Invoices => Set<Invoice>();

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
                // P2 — owner model (D15): owner_type/kind lưu string (GEN-2), owner_id ref lỏng → Auth.
                e.Property(x => x.OwnerType).HasConversion<string>().HasMaxLength(8).IsRequired();
                e.Property(x => x.OwnerId).IsRequired();
                e.Property(x => x.Kind).HasConversion<string>().HasMaxLength(20)
                 .HasDefaultValue(OrderKind.CreditPack);
                e.Property(x => x.AmountVnd).IsRequired();
                e.HasIndex(x => x.PayosOrderCode).IsUnique();
                e.Property(x => x.CreatedAt).HasDefaultValueSql("now()");

                // P8b — package optional: đơn InvoiceSettlement không gắn package (gắn invoice_id).
                e.HasOne(x => x.Package)
                 .WithMany(x => x.Orders)
                 .HasForeignKey(x => x.PackageId)
                 .IsRequired(false)
                 .OnDelete(DeleteBehavior.Restrict);

                // P8b — 1 Invoice ── N Order (orders.invoice_id): N lần tất toán/retry cùng hóa đơn.
                // Restrict: không xoá hóa đơn còn đơn tất toán tham chiếu (giữ vết tiền).
                e.HasOne(x => x.Invoice)
                 .WithMany(x => x.Orders)
                 .HasForeignKey(x => x.InvoiceId)
                 .IsRequired(false)
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
                e.HasIndex(x => new { x.OrderId, x.CreatedAt });

                // N–1 (payment.md): 1 order có NHIỀU payment_transactions. order_id nullable — webhook
                // không khớp đơn nào vẫn lưu bằng chứng (order_id null). SetNull khi xoá order (giữ log).
                e.HasOne(x => x.Order)
                 .WithMany(x => x.PaymentTransactions)
                 .HasForeignKey(x => x.OrderId)
                 .IsRequired(false)
                 .OnDelete(DeleteBehavior.SetNull);
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

            // ── Invoice (P8b — hóa đơn postpaid, CHỈ Org) ───────────
            modelBuilder.Entity<Invoice>(e =>
            {
                e.ToTable("invoices");
                e.HasKey(x => x.Id);
                e.Property(x => x.Id).HasDefaultValueSql("gen_random_uuid()");

                e.Property(x => x.OwnerType).HasConversion<string>().HasMaxLength(8).IsRequired();
                e.Property(x => x.OwnerId).IsRequired();

                // Tiền lưu numeric(16,2) (đừng để default lệch — Context7 EF Core HasPrecision).
                e.Property(x => x.UnitPrice).HasPrecision(16, 2);
                e.Property(x => x.Amount).HasPrecision(16, 2);

                e.Property(x => x.Status).HasConversion<string>().HasMaxLength(16)
                 .HasDefaultValue(InvoiceStatus.Issued);

                e.Property(x => x.CreatedAt).HasDefaultValueSql("now()");

                e.HasIndex(x => new { x.OwnerType, x.OwnerId });
            });
        }
    }
}
