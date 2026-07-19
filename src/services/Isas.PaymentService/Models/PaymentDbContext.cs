using Microsoft.EntityFrameworkCore;

namespace PaymentService.Models
{
    public class PaymentDbContext : DbContext
    {
        public PaymentDbContext(DbContextOptions<PaymentDbContext> options) : base(options) { }

        public DbSet<ProductPackage> ProductPackages => Set<ProductPackage>();
        public DbSet<Order> Orders => Set<Order>();
        public DbSet<PaymentTransaction> PaymentTransactions => Set<PaymentTransaction>();
        public DbSet<CreditAccount> CreditAccounts => Set<CreditAccount>();
        public DbSet<CreditReservation> CreditReservations => Set<CreditReservation>();
        public DbSet<CreditTransaction> CreditTransactions => Set<CreditTransaction>();
        public DbSet<Invoice> Invoices => Set<Invoice>();
        // F8 — bảng dựng lại sau khi DB15 drop bản scaffold chết; lần này có đường tiêu thụ thật.
        public DbSet<Subscription> Subscriptions => Set<Subscription>();

        // DB14 — đóng dấu updated_at TỰ ĐỘNG cho mọi entity IHasUpdatedAt bị SỬA (Modified). SaveChanges()
        // parameterless của EF gọi xuống overload (bool) này nên chỉ cần override 2 overload dưới là đủ mọi
        // đường ghi tracked. LƯU Ý: ExecuteUpdateAsync KHÔNG đi qua SaveChanges → các call set-based flip
        // orders.status / credit_reservations.status tự thêm .SetProperty(UpdatedAt) (xem WebhookService,
        // OrderStatusService, CreditAccountService — giống credit_accounts đã làm).
        public override int SaveChanges(bool acceptAllChangesOnSuccess)
        {
            StampUpdatedAt();
            return base.SaveChanges(acceptAllChangesOnSuccess);
        }

        public override Task<int> SaveChangesAsync(
            bool acceptAllChangesOnSuccess, CancellationToken cancellationToken = default)
        {
            StampUpdatedAt();
            return base.SaveChangesAsync(acceptAllChangesOnSuccess, cancellationToken);
        }

        private void StampUpdatedAt()
        {
            var now = DateTime.UtcNow;
            foreach (var entry in ChangeTracker.Entries<IHasUpdatedAt>())
            {
                if (entry.State == EntityState.Modified)
                    entry.Entity.UpdatedAt = now;
            }
        }

        protected override void OnModelCreating(ModelBuilder modelBuilder)
        {
            // ── ProductPackage ────────────────────────────────────
            modelBuilder.Entity<ProductPackage>(e =>
            {
                e.HasKey(x => x.Id);
                e.Property(x => x.Id).HasDefaultValueSql("gen_random_uuid()");
                e.Property(x => x.Name).IsRequired();
                // DB14 — enum lưu STRING (GEN-2), khớp mọi enum khác. Trước đây thiếu HasConversion →
                // persist thành int. varchar(20) đủ chứa "OneTime"/"Subscription". Migration ALTER int→string
                // phải hand-write USING (không auto-convert dữ liệu int cũ) — xem AddAuditColumnsAndTypes.
                e.Property(x => x.Type).HasConversion<string>().HasMaxLength(20).IsRequired();
                e.Property(x => x.PriceVnd).IsRequired();
                e.Property(x => x.IsActive).HasDefaultValue(true);
                e.Property(x => x.CreatedAt).HasDefaultValueSql("now()");
                // DB14 — audit updated_at (mirror created_at style: default now() ở DB; C# init ở entity để
                // insert SQLite/EnsureCreated không phụ thuộc now()). Stamp tự động khi Modified (SaveChanges).
                e.Property(x => x.UpdatedAt).HasDefaultValueSql("now()");
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
                // DB14 — HasMaxLength(20): status trước là text (không maxlen) → varchar(20). Enum name dài
                // nhất "Cancelled" (9) → 20 dư. Giữ nguyên HasConversion<string> + default Pending.
                e.Property(x => x.Status).HasConversion<string>().HasMaxLength(20)
                 .HasDefaultValue(OrderStatus.Pending);
                // P2 — owner model (D15): owner_type/kind lưu string (GEN-2), owner_id ref lỏng → Auth.
                e.Property(x => x.OwnerType).HasConversion<string>().HasMaxLength(8).IsRequired();
                e.Property(x => x.OwnerId).IsRequired();
                e.Property(x => x.Kind).HasConversion<string>().HasMaxLength(20)
                 .HasDefaultValue(OrderKind.CreditPack);
                e.Property(x => x.AmountVnd).IsRequired();
                e.HasIndex(x => x.PayosOrderCode).IsUnique();

                // DB26 — trước vòng này `orders` KHÔNG có index nào trên cột owner: mọi lần user/org mở
                // trang đơn hàng là seq scan toàn bảng + sort. Khớp đúng hình dạng
                // GetOwnerOrdersAsync: lọc (owner_type, owner_id) rồi ORDER BY created_at DESC.
                // Đuôi (created_at, id) cũng đúng shape keyset DB8 nếu sau này phân trang theo chủ ví.
                e.HasIndex(x => new { x.OwnerType, x.OwnerId, x.CreatedAt, x.Id })
                 .IsDescending(false, false, true, true)
                 .HasDatabaseName("ix_orders_owner_created");

                // DB26 — OrderExpiryReconciler quét mỗi 5': WHERE status='Pending' AND expired_at < cutoff
                // ORDER BY expired_at. Không index → full scan `orders` mỗi vòng, càng ngày càng chậm.
                // Partial: đơn Pending là thiểu số sống ngắn (đa số đơn nằm ở trạng thái terminal), nên
                // index chỉ ôm phần bảng sweeper thật sự cần. Literal 'Pending' khớp chuỗi enum lưu
                // (Status = HasConversion<string>), cột snake_case.
                e.HasIndex(x => x.ExpiredAt)
                 .HasDatabaseName("ix_orders_pending_expired_at")
                 .HasFilter("status = 'Pending'");

                // DB26 — admin oversight ListAllOrdersAsync (AUTH-7) keyset (created_at DESC, id DESC),
                // KHÔNG lọc owner nên index owner ở trên không phục vụ được. Không có index này thì công
                // phân trang keyset DB8 vẫn phải sort toàn bảng mỗi trang. Đơn hàng ghi theo nhịp mua
                // (thấp) nên 3 index trên `orders` là đánh đổi rẻ.
                e.HasIndex(x => new { x.CreatedAt, x.Id })
                 .IsDescending(true, true)
                 .HasDatabaseName("ix_orders_created_id_desc");
                e.Property(x => x.CreatedAt).HasDefaultValueSql("now()");
                // DB14 — audit updated_at (stamp khi Modified: Cancel/Paid webhook flip status). ExecuteUpdate
                // flip status tự thêm .SetProperty(UpdatedAt) (WebhookService); tracked Cancel qua SaveChanges.
                e.Property(x => x.UpdatedAt).HasDefaultValueSql("now()");

                // F18 — audit hoàn tiền. Lý do/mã cổng giới hạn độ dài để không thành bãi rác text.
                e.Property(x => x.RefundReason).HasMaxLength(500);
                e.Property(x => x.RefundGatewayRef).HasMaxLength(100);

                // F19 — báo cáo doanh thu gộp theo kỳ: WHERE status='Paid' AND paid_at ∈ [from,to).
                // Partial theo đúng vị ngữ truy vấn: đơn Paid là tập con của `orders` (Pending bỏ dở +
                // Expired chiếm phần lớn bảng theo thời gian), nên index chỉ ôm phần báo cáo cần đọc.
                // Literal 'Paid' khớp chuỗi enum lưu (Status = HasConversion<string>), cột snake_case —
                // cùng lối ix_orders_pending_expired_at (DB26).
                //
                // ⚠ Partial index chỉ được planner dùng khi nó CHỨNG MINH được predicate query ⇒ predicate
                // index. EF phải render `status` thành LITERAL chứ không phải tham số; đã khoá bằng test
                // đọc SQL sinh ra từ chính hàm production (bài học DB27: render thành @p thì index chết
                // trong im lặng — index vẫn tồn tại, EXPLAIN vẫn seq scan, không có gì báo lỗi).
                e.HasIndex(x => x.PaidAt)
                 .HasDatabaseName("ix_orders_paid_at")
                 .HasFilter("status = 'Paid'");



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

            // ── CreditAccount (P1 — ví của chủ sở hữu, D15) ─────────
            modelBuilder.Entity<CreditAccount>(e =>
            {
                // DB1 — số dư credit KHÔNG bao giờ âm (chống double-spend/bug logic tràn xuống dưới 0).
                // period_usage nullable → phải IS NULL OR ... tường minh (NULL >= 0 = UNKNOWN, không đủ chặn).
                // F7 — free_credits_granted vào cùng CHECK (cùng ngữ nghĩa "số dư không âm"). CỐ Ý KHÔNG
                // thêm vế kiểu `free_credits_granted >= remaining_credits`: đó đúng là lớp bug DB22 —
                // một bút toán số dư hợp lệ (Consume/Release) sẽ làm nổ CHECK bên trong transaction,
                // rollback → reservation kẹt Reserved → consumer nack-requeue vô hạn → nghẽn queue credit.
                e.ToTable("credit_accounts", t => t.HasCheckConstraint(
                    "ck_credit_accounts_non_negative",
                    "remaining_credits >= 0 AND reserved_credits >= 0 AND free_credits_granted >= 0 AND (period_usage IS NULL OR period_usage >= 0)"));
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
                e.Property(x => x.FreeCreditsGranted).HasDefaultValue(0);

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

                // F8 — nguồn chi trả, enum lưu string (GEN-2). Default 'Credit' để mọi row CŨ (và mọi
                // đường ghi chưa biết tới F8) giữ nguyên nghĩa "chỗ giữ này đã trừ ví" ⇒ Consume/Release
                // của chúng vẫn chạy đúng nhánh bút toán như trước.
                e.Property(x => x.FundedBy).HasConversion<string>().HasMaxLength(16)
                 .HasDefaultValue(ReservationFunding.Credit);

                e.Property(x => x.CreatedAt).HasDefaultValueSql("now()");
                // DB14 — audit updated_at (stamp khi status flip Reserved→Consumed/Released). 2 flip đó dùng
                // ExecuteUpdate (CreditAccountService) nên tự thêm .SetProperty(UpdatedAt) tại đó.
                e.Property(x => x.UpdatedAt).HasDefaultValueSql("now()");

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

                // F18 — self-FK: bút toán hoàn trỏ về bút toán mua gốc. Restrict (sổ cái append-only,
                // không row nào bị xoá). UNIQUE LỌC `WHERE reverses_transaction_id IS NOT NULL` = khoá
                // idempotency chống hoàn hai lần cùng một khoản mua: hai request hoàn song song thì bên
                // thua đụng UNIQUE lúc SaveChanges → rollback → trả AlreadyRefunded, thay vì cả hai cùng
                // trừ ví. NULL không bị ràng buộc nên mọi bút toán không-hoàn vẫn tự do.
                e.HasOne(x => x.ReversesTransaction)
                 .WithMany()
                 .HasForeignKey(x => x.ReversesTransactionId)
                 .IsRequired(false)
                 .OnDelete(DeleteBehavior.Restrict);

                e.HasIndex(x => x.ReversesTransactionId)
                 .IsUnique()
                 .HasDatabaseName("ux_credit_transactions_reverses")
                 .HasFilter("reverses_transaction_id IS NOT NULL");

                // F19 — `GET /payment/me/credit-transactions` lọc (owner_type, owner_id) rồi
                // ORDER BY created_at DESC, id DESC (keyset DB8). Index composite DB9 hiện có chỉ phục vụ
                // FK lookup, KHÔNG mang khoá sắp xếp ⇒ thiếu index này thì mỗi trang phải sort lại toàn bộ
                // sổ cái của chủ ví. Cùng hình dạng ix_orders_owner_created (DB26).
                // Sổ cái là bảng ghi nhiều nhất trong service (1 row/lượt chấm) nên đây là index đáng giá
                // nhất của F19.
                e.HasIndex(x => new { x.OwnerType, x.OwnerId, x.CreatedAt, x.Id })
                 .IsDescending(false, false, true, true)
                 .HasDatabaseName("ix_credit_transactions_owner_created");


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

            // ── Subscription (F8 — thuê bao Premium B2C / membership B2B) ─
            modelBuilder.Entity<Subscription>(e =>
            {
                // Kỳ hạn phải có bề rộng dương. CHECK này an toàn theo nghĩa DB22: không có đường ghi nào
                // SỬA started_at/expires_at của row đã tồn tại (append-one-row-per-order), nên nó chỉ có thể
                // chặn lúc INSERT — không thể nổ giữa một transaction Consume/Release rồi kẹt reservation.
                e.ToTable("subscriptions", t => t.HasCheckConstraint(
                    "ck_subscriptions_period_positive", "expires_at > started_at"));
                e.HasKey(x => x.Id);
                e.Property(x => x.Id).HasDefaultValueSql("gen_random_uuid()");

                e.Property(x => x.OwnerType).HasConversion<string>().HasMaxLength(8).IsRequired();
                e.Property(x => x.OwnerId).IsRequired();

                e.Property(x => x.BillingCycle).HasConversion<string>().HasMaxLength(16).IsRequired();
                e.Property(x => x.Status).HasConversion<string>().HasMaxLength(16)
                 .HasDefaultValue(SubscriptionStatus.Active);

                e.Property(x => x.StartedAt).IsRequired();
                e.Property(x => x.ExpiresAt).IsRequired();
                e.Property(x => x.CreatedAt).HasDefaultValueSql("now()");
                e.Property(x => x.UpdatedAt).HasDefaultValueSql("now()");

                // Khoá idempotency của webhook: 1 đơn ⇒ tối đa 1 kỳ hạn. Filtered vì order_id nullable
                // (chỗ dành cho kỳ hạn cấp tay/khuyến mãi sau này, không sinh từ đơn nào).
                e.HasIndex(x => x.OrderId).IsUnique().HasFilter("order_id IS NOT NULL");

                // Đường nóng: MỌI lần reserve đều hỏi "chủ ví này còn thuê bao không". Partial theo
                // status='Active' (mẫu DB5) để index chỉ ôm phần bảng thật sự bị hỏi — kỳ đã hết hạn/huỷ
                // tích luỹ mãi mãi nhưng không bao giờ nằm trong vị ngữ này. Cột snake_case + literal
                // khớp tên member enum (Status = HasConversion<string>).
                e.HasIndex(x => new { x.OwnerType, x.OwnerId, x.ExpiresAt })
                 .HasDatabaseName("ix_subscriptions_owner_active")
                 .HasFilter("status = 'Active'");

                // DB5 — sweeper ExpireDueAsync quét WHERE status='Active' AND expires_at <= now.
                e.HasIndex(x => x.ExpiresAt)
                 .HasDatabaseName("ix_subscriptions_active_expires_at")
                 .HasFilter("status = 'Active'");

                e.HasOne(x => x.Package)
                 .WithMany()
                 .HasForeignKey(x => x.PackageId)
                 .IsRequired(false)
                 .OnDelete(DeleteBehavior.Restrict);

                e.HasOne(x => x.Order)
                 .WithMany()
                 .HasForeignKey(x => x.OrderId)
                 .IsRequired(false)
                 .OnDelete(DeleteBehavior.Restrict);

                // DB9 — FK nội-service composite (owner_type, owner_id) → credit_accounts (Restrict),
                // đồng nhất với reservations/transactions/invoices. Hệ quả CÓ CHỦ Ý: kích hoạt thuê bao
                // phải bảo đảm ví tồn tại trước — đằng nào reservation cũng bị chính FK này bắt buộc, nên
                // tạo ví ngay lúc kích hoạt tốt hơn là để người mua gói tháng ăn 402 ở buổi đầu tiên.
                e.HasOne<CreditAccount>()
                 .WithMany()
                 .HasForeignKey(x => new { x.OwnerType, x.OwnerId })
                 .HasPrincipalKey(a => new { a.OwnerType, a.OwnerId })
                 .OnDelete(DeleteBehavior.Restrict);
            });

            // ── DB10 — OPTIMISTIC CONCURRENCY (xmin) ────────────────
            // Defense-in-depth (user-approved): dùng cột hệ thống Postgres `xmin` làm concurrency token cho
            // credit_accounts (ví tiền). Map tường minh property ẩn `xmin` (uint) → cột hệ thống `xid` theo
            // doc Npgsql (efcore.pg 10 đã bỏ helper UseXminAsConcurrencyToken()). `xmin` là system column nên
            // migration KHÔNG phát AddColumn/DropColumn (model-snapshot-only). GATE IsNpgsql — `xmin` không có
            // tương đương SQLite → nhánh này bị bỏ qua dưới provider SQLite (EnsureCreated test) nên giữ nguyên
            // hành vi. (Introduce idiom IsNpgsql ở Payment — chưa có tiền lệ.)
            // LƯU Ý: credit_accounts hiện KHÔNG có đường ghi tracked read-modify-write (mọi mutation là
            // ExecuteUpdate...WHERE atomic) → xmin INERT lúc chạy; khai đồng nhất/phòng thủ nếu sau này có
            // đường ghi tracked. KHÔNG đổi CreditAccountService/WebhookService/reconciler.
            if (Database.IsNpgsql())
            {
                modelBuilder.Entity<CreditAccount>().Property<uint>("xmin")
                    .HasColumnName("xmin").HasColumnType("xid")
                    .ValueGeneratedOnAddOrUpdate().IsConcurrencyToken();
            }
        }
    }
}
