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
                // DB1 — số dư credit KHÔNG bao giờ âm (chống double-spend/bug logic tràn xuống dưới 0).
                // period_usage nullable → phải IS NULL OR ... tường minh (NULL >= 0 = UNKNOWN, không đủ chặn).
                e.ToTable("credit_accounts", t => t.HasCheckConstraint(
                    "ck_credit_accounts_non_negative",
                    "remaining_credits >= 0 AND reserved_credits >= 0 AND (period_usage IS NULL OR period_usage >= 0)"));
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

                // 1 ví / (owner_type, owner_id) — §DB payment.md.
                // DB9 — ALTERNATE KEY (không chỉ UNIQUE INDEX): Postgres FK không ref được unique index,
                // phải là UNIQUE CONSTRAINT. HasAlternateKey emit UNIQUE CONSTRAINT → làm principal key cho
                // 3 FK nội-service (reservations/transactions/invoices → credit_accounts). Alt-key đã cho
                // tính duy nhất nên bỏ HasIndex(...).IsUnique() cũ (tránh index+constraint trùng lặp).
                e.HasAlternateKey(x => new { x.OwnerType, x.OwnerId });
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

                // DB9 FK support index (owner_type, owner_id) — DECLARE TƯỜNG MINH để giữ lại. FK
                // composite (owner_type, owner_id)→credit_accounts (bên dưới) sinh index này qua convention.
                // DB5 thêm index partial (owner_type, owner_id, created_at) có prefix trùng → EF model-differ
                // coi index convention là "thừa" và DROP nó. Nhưng index DB5 là PARTIAL (chỉ row Reserved) →
                // KHÔNG phủ được FK/lookup mọi-trạng-thái. Khai tường minh (index EXPLICIT không bị auto-remove)
                // → migration chỉ ADDITIVE (giữ FK index đầy đủ, KHÔNG drop). Cùng cột/tên convention nên
                // KHÔNG tạo index trùng — vẫn đúng 1 index (owner_type, owner_id) đầy đủ.
                e.HasIndex(x => new { x.OwnerType, x.OwnerId });

                // DB5 — index cho 2 reconciler background quét credit_reservations (trước chỉ có FK
                // index (owner_type, owner_id) + unique(session_id) → full-scan phần status/created_at):
                //   • CreditReservationReconciler: CountAsync(owner_type=X, owner_id=Y, status=Reserved)
                //     → prefix (owner_type, owner_id) phục vụ count per-account.
                //   • OrphanReservationReconciler: Where(status=Reserved, created_at < cutoff).OrderBy(created_at)
                //     → tail created_at phục vụ age-filter + order.
                // PARTIAL "status = 'Reserved'" giữ index tí hon (chỉ reservation đang giữ chỗ — Consumed/
                // Released không index) → 1 index hình-vị-ngữ (predicate-shaped) phủ CẢ HAI reconciler.
                // Filter dùng tên cột snake_case 'status' (khớp UseSnakeCaseNamingConvention) + literal
                // 'Reserved' = tên member enum (Status lưu string qua HasConversion<string>, dòng trên).
                // Postgres + SQLite(>=3.8) đều hỗ trợ partial index (DB2 outbox precedent).
                e.HasIndex(x => new { x.OwnerType, x.OwnerId, x.CreatedAt })
                 .HasDatabaseName("ix_credit_reservations_reserved")
                 .HasFilter("status = 'Reserved'");

                // DB9 — FK nội-service composite (owner_type, owner_id) → credit_accounts.
                // Không nav (CreditReservation không có prop tới account) → dạng HasOne<CreditAccount>().
                // Restrict: owner NOT NULL không SetNull được; ví không bao giờ bị xoá.
                // (session_id là ref XUYÊN service → Interview, giữ Guid lỏng, KHÔNG FK.)
                e.HasOne<CreditAccount>()
                 .WithMany()
                 .HasForeignKey(x => new { x.OwnerType, x.OwnerId })
                 .HasPrincipalKey(a => new { a.OwnerType, a.OwnerId })
                 .OnDelete(DeleteBehavior.Restrict);
            });

            // ── CreditTransaction (P1 — sổ cái, refactor owner_type) ─
            modelBuilder.Entity<CreditTransaction>(e =>
            {
                // DB1 — sổ cái append-only: mọi bút toán phải chuyển số dư (Purchase +N / Consume −1 / Refund).
                // delta = 0 là bút toán vô nghĩa → chặn ở DB (dữ liệu rác/bug ghi sổ).
                e.ToTable("credit_transactions", t => t.HasCheckConstraint(
                    "ck_credit_transactions_delta_nonzero", "delta <> 0"));
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

                // DB9 — FK nội-service composite (owner_type, owner_id) → credit_accounts (Restrict).
                // Không nav; owner NOT NULL. (order_id = FK cùng-service sẵn ở trên; session_id ref XUYÊN
                // service → Interview giữ Guid lỏng, KHÔNG FK.)
                e.HasOne<CreditAccount>()
                 .WithMany()
                 .HasForeignKey(x => new { x.OwnerType, x.OwnerId })
                 .HasPrincipalKey(a => new { a.OwnerType, a.OwnerId })
                 .OnDelete(DeleteBehavior.Restrict);
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

                // DB9 — FK nội-service composite (owner_type, owner_id) → credit_accounts (Restrict).
                // Invoice CHỈ Org — dùng owner đồng nhất (owner NOT NULL), KHÔNG dùng account_id (ref lỏng
                // giữ nguyên cho tương thích). Không nav; ví không bao giờ bị xoá.
                e.HasOne<CreditAccount>()
                 .WithMany()
                 .HasForeignKey(x => new { x.OwnerType, x.OwnerId })
                 .HasPrincipalKey(a => new { a.OwnerType, a.OwnerId })
                 .OnDelete(DeleteBehavior.Restrict);
            });
        }
    }
}
