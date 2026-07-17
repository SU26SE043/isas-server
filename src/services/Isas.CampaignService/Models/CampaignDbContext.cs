using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.ChangeTracking;
using Microsoft.EntityFrameworkCore.Storage.ValueConversion;
using System.Text.Json;

namespace Isas.CampaignService.Models
{
    public class CampaignDbContext : DbContext
    {
        public CampaignDbContext(DbContextOptions<CampaignDbContext> options) : base(options) { }

        public DbSet<Campaign> Campaigns => Set<Campaign>();
        public DbSet<CampaignQuestion> CampaignQuestions => Set<CampaignQuestion>();
        public DbSet<CampaignCriterion> CampaignCriteria => Set<CampaignCriterion>();
        public DbSet<AuditLog> AuditLogs => Set<AuditLog>();
        public DbSet<CampaignInvitation> CampaignInvitations => Set<CampaignInvitation>();
        public DbSet<CampaignRanking> CampaignRankings => Set<CampaignRanking>();
        public DbSet<CvSubmission> CvSubmissions => Set<CvSubmission>();                         // C13: sàng CV (DB16, ex campaign_candidates)
        public DbSet<CampaignMembership> CampaignMemberships => Set<CampaignMembership>();        // D2: membership ứng viên↔campaign (DB16)
        public DbSet<CandidateCriterionScore> CandidateCriterionScores => Set<CandidateCriterionScore>();
        public DbSet<SessionFlag> SessionFlags => Set<SessionFlag>();                            // SEC-1: cờ chống gian lận cho HR
        public DbSet<OutboxMessage> OutboxMessages => Set<OutboxMessage>();                      // DB2b: transactional outbox (invitation-email)

        // C13: string[] ↔ JSON (jsonb trên Npgsql; text trên SQLite test). Portable — filter đọc/ghi trong C#,
        // không query trong JSON. Comparer để EF theo dõi thay đổi phần tử đúng (list là mutable reference).
        private static readonly ValueConverter<List<string>?, string?> StringListConverter = new(
            v => v == null ? null : JsonSerializer.Serialize(v, (JsonSerializerOptions?)null),
            v => v == null ? null : JsonSerializer.Deserialize<List<string>>(v, (JsonSerializerOptions?)null));

        private static readonly ValueComparer<List<string>?> StringListComparer = new(
            (a, b) => (a == null && b == null) || (a != null && b != null && a.SequenceEqual(b)),
            v => v == null ? 0 : v.Aggregate(0, (h, s) => HashCode.Combine(h, s.GetHashCode())),
            v => v == null ? null : v.ToList());

        protected override void OnModelCreating(ModelBuilder modelBuilder)
        {
            // ── Campaign ──────────────────────────────────────────────────
            modelBuilder.Entity<Campaign>(e =>
            {
                e.ToTable("campaigns");
                e.HasKey(x => x.Id);
                e.Property(x => x.Id).HasDefaultValueSql("gen_random_uuid()");

                e.Property(x => x.Title).HasMaxLength(255).IsRequired();
                e.Property(x => x.Domain).HasMaxLength(100);

                e.Property(x => x.Status)
                 .HasConversion<string>()
                 .HasMaxLength(20)
                 .HasDefaultValue(CampaignStatus.Draft);

                e.Property(x => x.AntiCheatEnabled).HasDefaultValue(true);
                e.Property(x => x.FaceVerifyEnabled).HasDefaultValue(false);   // SEC-1: face-verify opt-in (B2B)

                e.Property(x => x.StartsAt).IsRequired();

                e.Property(x => x.CreatedAt).HasDefaultValueSql("now()");
                e.Property(x => x.UpdatedAt).HasDefaultValueSql("now()");

                // Soft delete (D11): mọi query tự lọc deleted_at IS NULL
                e.HasQueryFilter(x => x.DeletedAt == null);

                // C13: rule cứng sàng CV — string[] lưu jsonb (Npgsql) / text (SQLite) qua converter.
                e.Property(x => x.RequiredSkills).HasConversion(StringListConverter, StringListComparer);
                e.Property(x => x.KeywordsAny).HasConversion(StringListConverter, StringListComparer);
                if (Database.IsNpgsql())
                {
                    e.Property(x => x.RequiredSkills).HasColumnType("jsonb");
                    e.Property(x => x.KeywordsAny).HasColumnType("jsonb");
                }

                // Indexes — lọc theo owner ORG (BK4)
                e.HasIndex(x => new { x.OrgId, x.Status });
                e.HasIndex(x => new { x.OrgId, x.CreatedAt });
            });

            // ── CampaignQuestion ─────────────────────────────────────────
            modelBuilder.Entity<CampaignQuestion>(e =>
            {
                e.ToTable("campaign_questions");
                e.HasKey(x => x.Id);
                e.Property(x => x.Id).HasDefaultValueSql("gen_random_uuid()");

                e.Property(x => x.QuestionText).IsRequired();
                e.Property(x => x.IsRequired).HasDefaultValue(true);

                e.Property(x => x.Source)
                 .HasConversion<string>()
                 .HasMaxLength(20);

                e.Property(x => x.CreatedAt).HasDefaultValueSql("now()");

                // DB13: khớp soft-delete filter của Campaign (required nav) → hết cảnh báo
                // PossibleIncorrectRequiredNavigationWithQueryFilterInteractionWarning + không đọc con mồ côi.
                e.HasQueryFilter(x => x.Campaign.DeletedAt == null);

                e.HasOne(x => x.Campaign)
                 .WithMany(x => x.Questions)
                 .HasForeignKey(x => x.CampaignId)
                 .OnDelete(DeleteBehavior.Cascade);
            });

            // ── CampaignCriterion (tiêu chí có cấu trúc — C8/D9) ─────────────
            modelBuilder.Entity<CampaignCriterion>(e =>
            {
                e.ToTable("campaign_criteria");
                e.HasKey(x => x.Id);
                e.Property(x => x.Id).HasDefaultValueSql("gen_random_uuid()");
                e.Property(x => x.Name).IsRequired().HasMaxLength(255);
                e.Property(x => x.Weight).HasColumnType("numeric(5,4)");
                e.Property(x => x.Source).HasConversion<string>().HasMaxLength(20);
                e.Property(x => x.CreatedAt).HasDefaultValueSql("now()");
                e.Property(x => x.UpdatedAt).HasDefaultValueSql("now()");   // C12

                // C12: tiêu chí structured HR khai thẳng — chống trùng + giữ thứ tự hiển thị.
                e.HasIndex(x => new { x.CampaignId, x.OrderNo }).IsUnique();
                e.HasIndex(x => new { x.CampaignId, x.Name }).IsUnique();

                // DB13: khớp soft-delete filter của Campaign (required nav).
                e.HasQueryFilter(x => x.Campaign.DeletedAt == null);

                e.HasOne(x => x.Campaign)
                 .WithMany(x => x.Criteria)
                 .HasForeignKey(x => x.CampaignId)
                 .OnDelete(DeleteBehavior.Cascade);
            });

            // ── AuditLog (vết thao tác — C10/D11) ───────────────────────────
            modelBuilder.Entity<AuditLog>(e =>
            {
                e.ToTable("audit_logs");
                e.HasKey(x => x.Id);
                e.Property(x => x.Id).HasDefaultValueSql("gen_random_uuid()");
                e.Property(x => x.Entity).IsRequired().HasMaxLength(64);
                e.Property(x => x.Action).HasConversion<string>().HasMaxLength(32);
                e.Property(x => x.At).HasDefaultValueSql("now()");
                e.HasIndex(x => new { x.EntityId, x.At });
            });

            // ── CampaignInvitation (magic-link mời — D1, đường 1: mời thẳng email) ──
            modelBuilder.Entity<CampaignInvitation>(e =>
            {
                e.ToTable("campaign_invitations");
                e.HasKey(x => x.Id);
                e.Property(x => x.Id).HasDefaultValueSql("gen_random_uuid()");
                e.Property(x => x.Token).IsRequired().HasMaxLength(128);
                e.Property(x => x.Email).IsRequired().HasMaxLength(255);
                e.Property(x => x.CreatedAt).HasDefaultValueSql("now()");

                e.HasIndex(x => x.Token).IsUnique();
                e.HasIndex(x => x.CampaignId);

                // DB13: khớp soft-delete filter của Campaign (required nav).
                e.HasQueryFilter(x => x.Campaign.DeletedAt == null);

                e.HasOne(x => x.Campaign)
                 .WithMany(x => x.Invitations)
                 .HasForeignKey(x => x.CampaignId)
                 .OnDelete(DeleteBehavior.Cascade);

                // DB9/DB16: FK nội-service invitation → cv_submission (đường-2 từ shortlist). Optional
                // (campaign_candidate_id nullable; đường-1 mời-thẳng = null). SetNull: xoá CV →
                // invitation giữ lại, chỉ mất link shortlist. Optional nav → KHÔNG cần query filter mới.
                // Cột DB giữ tên campaign_candidate_id (không rename cột); nav re-point về CvSubmission.
                e.HasOne(x => x.CvSubmission)
                 .WithMany()
                 .HasForeignKey(x => x.CampaignCandidateId)
                 .HasConstraintName("fk_campaign_invitations_cv_submission_campaign_candidate_id")
                 .OnDelete(DeleteBehavior.SetNull);
            });

            // ── CampaignRanking (read-model B2B — E4/D10, event SessionScored) ─────
            modelBuilder.Entity<CampaignRanking>(e =>
            {
                e.ToTable("campaign_rankings");
                e.HasKey(x => x.Id);
                e.Property(x => x.Id).HasDefaultValueSql("gen_random_uuid()");
                e.Property(x => x.TotalScore).HasColumnType("numeric(5,2)");
                e.Property(x => x.UpdatedAt).HasDefaultValueSql("now()");
                // E11b — HR override (nullable; null = chưa chốt tay).
                e.Property(x => x.OverrideScore).HasColumnType("numeric(5,2)");
                e.Property(x => x.OverrideResult).HasMaxLength(10);

                // Idempotent upsert theo session_id: event tới 2 lần vẫn 1 row.
                e.HasIndex(x => x.SessionId).IsUnique();
                e.HasIndex(x => new { x.CampaignId, x.TotalScore });

                // DB13/DB9: required nav (CampaignId NOT NULL) tới Campaign có soft-delete filter →
                // BẮT BUỘC khớp filter, nếu không EF phát PossibleIncorrectRequiredNavigation warning +
                // đọc ranking mồ côi (campaign đã soft-delete). Đọc ranking join campaigns tự ẩn.
                e.HasQueryFilter(x => x.Campaign.DeletedAt == null);

                // DB9: FK nội-service campaign_rankings.campaign_id → campaigns.id. Restrict (bảo vệ
                // read-model ranking; campaign vốn soft-delete nên cascade không kích hoạt). CandidateId/
                // SessionId = ref XUYÊN service → giữ Guid lỏng (GEN-2), KHÔNG FK.
                e.HasOne(x => x.Campaign)
                 .WithMany()
                 .HasForeignKey(x => x.CampaignId)
                 .OnDelete(DeleteBehavior.Restrict);
            });

            // ── CvSubmission (sàng CV B2B — C13/D18; DB16 ex campaign_candidates) ───
            modelBuilder.Entity<CvSubmission>(e =>
            {
                e.ToTable("cv_submission");
                e.HasKey(x => x.Id);
                e.Property(x => x.Id).HasDefaultValueSql("gen_random_uuid()");
                e.Property(x => x.FullName).HasMaxLength(255);
                e.Property(x => x.Email).HasMaxLength(255);

                e.Property(x => x.ParseStatus).HasConversion<string>().HasMaxLength(16);
                e.Property(x => x.Status).HasConversion<string>().HasMaxLength(20);
                e.Property(x => x.YearsExperience).HasColumnType("numeric(4,1)");

                e.Property(x => x.Skills).HasConversion(StringListConverter, StringListComparer);
                if (Database.IsNpgsql())
                    e.Property(x => x.Skills).HasColumnType("jsonb");

                e.Property(x => x.CreatedAt).HasDefaultValueSql("now()");
                e.Property(x => x.UpdatedAt).HasDefaultValueSql("now()");

                // UNIQUE(campaign_id, email) — chống trùng ứng viên trong campaign. NULL distinct
                // (Postgres & SQLite) → nhiều CV không tách được email vẫn insert được.
                e.HasIndex(x => new { x.CampaignId, x.Email }).IsUnique();
                e.HasIndex(x => new { x.CampaignId, x.Status });

                // DB5: index cho StuckScreeningRepublisher (C15) — sweeper quét mỗi 2' theo predicate
                // (Status, LastScreeningPublishedAt) KHÔNG có campaign_id → index (campaign_id, status)
                // ở trên vô dụng (leading col không khớp). Non-partial: cả 2 nhánh sweeper (Filtered+null,
                // Analyzing+not-null) đều key theo status (cột dẫn đầu, selective — chỉ Filtered/Analyzing
                // là hot); LastScreeningPublishedAt cột phụ để so mốc. Status lưu string (HasConversion)
                // → không cần filter literal. DB16 — đổi tên theo bảng mới (cv_submission).
                e.HasIndex(x => new { x.Status, x.LastScreeningPublishedAt })
                 .HasDatabaseName("ix_cv_submission_status_lsp");

                // DB13: khớp soft-delete filter của Campaign (required nav).
                e.HasQueryFilter(x => x.Campaign.DeletedAt == null);

                e.HasOne(x => x.Campaign)
                 .WithMany(x => x.CvSubmissions)
                 .HasForeignKey(x => x.CampaignId)
                 .OnDelete(DeleteBehavior.Cascade);
            });

            // ── CampaignMembership (D2 join — DB16 tách khỏi bảng God) ─────────────
            modelBuilder.Entity<CampaignMembership>(e =>
            {
                e.ToTable("campaign_membership");
                e.HasKey(x => x.Id);
                e.Property(x => x.Id).HasDefaultValueSql("gen_random_uuid()");

                e.Property(x => x.Status).HasConversion<string>().HasMaxLength(20).HasDefaultValue(MembershipStatus.Joined);
                // interview_status enum string (nullable = NotStarted).
                e.Property(x => x.InterviewStatus).HasConversion<string>().HasMaxLength(16);
                e.Property(x => x.CreatedAt).HasDefaultValueSql("now()");
                e.Property(x => x.UpdatedAt).HasDefaultValueSql("now()");

                // 1 membership / (campaign, candidate) — chống join 2 lần (D2 idempotent).
                e.HasIndex(x => new { x.CampaignId, x.CandidateId }).IsUnique();
                e.HasIndex(x => x.CandidateId);
                // 1 membership / CV shortlist (đường-2). NULL distinct → nhiều đường-1 (không CV) vẫn insert.
                e.HasIndex(x => x.CvSubmissionId).IsUnique();

                // DB13: required nav → Campaign (soft-delete filter) → BẮT BUỘC khớp filter.
                e.HasQueryFilter(x => x.Campaign.DeletedAt == null);

                e.HasOne(x => x.Campaign)
                 .WithMany()
                 .HasForeignKey(x => x.CampaignId)
                 .OnDelete(DeleteBehavior.Cascade);

                // FK nội-service membership.cv_submission_id → cv_submission.id. OPTIONAL (nullable,
                // đường-1 mời-thẳng = null) → SetNull: xoá CV chỉ mất link shortlist, membership giữ.
                // Optional nav → KHÔNG cần query filter (D2 đường-1 không có CV).
                e.HasOne(x => x.CvSubmission)
                 .WithMany()
                 .HasForeignKey(x => x.CvSubmissionId)
                 .OnDelete(DeleteBehavior.SetNull);
            });

            // ── CandidateCriterionScore (điểm khớp/tiêu chí — C14, bảng tạo ở C13) ──
            modelBuilder.Entity<CandidateCriterionScore>(e =>
            {
                e.ToTable("candidate_criterion_scores");
                e.HasKey(x => x.Id);
                e.Property(x => x.Id).HasDefaultValueSql("gen_random_uuid()");
                e.Property(x => x.MatchScore).HasColumnType("numeric(5,2)");
                e.Property(x => x.CreatedAt).HasDefaultValueSql("now()");

                e.HasIndex(x => new { x.CandidateId, x.CriterionId }).IsUnique();

                // DB13: CvSubmission + CampaignCriterion (2 required nav bên dưới) đã có soft-delete
                // filter → phải khớp filter ở đây, nếu không EF phát lại warning + đọc điểm mồ côi.
                // Chained qua CvSubmission→Campaign (Criterion cùng campaign → 1 điều kiện là đủ).
                e.HasQueryFilter(x => x.CvSubmission.Campaign.DeletedAt == null);

                // DB16 — nav re-point về CvSubmission; cột FK giữ tên candidate_id (không rename cột).
                e.HasOne(x => x.CvSubmission)
                 .WithMany(x => x.CriterionScores)
                 .HasForeignKey(x => x.CandidateId)
                 .HasConstraintName("fk_candidate_criterion_scores_cv_submission_candidate_id")
                 .OnDelete(DeleteBehavior.Cascade);

                // Restrict: chặn xoá tiêu chí còn điểm tham chiếu (TÁI DÙNG rubric campaign_criteria).
                e.HasOne(x => x.Criterion)
                 .WithMany()
                 .HasForeignKey(x => x.CriterionId)
                 .OnDelete(DeleteBehavior.Restrict);
            });

            // ── SessionFlag (cờ chống gian lận cho HR — SEC-1/D13) ─────────────────
            modelBuilder.Entity<SessionFlag>(e =>
            {
                e.ToTable("session_flags");
                e.HasKey(x => x.Id);
                e.Property(x => x.Id).HasDefaultValueSql("gen_random_uuid()");
                e.Property(x => x.SignalType).IsRequired().HasMaxLength(32);
                e.Property(x => x.DetectedAt).HasDefaultValueSql("now()");

                // Gom cờ theo buổi (surface cho HR + aggregate results). SessionId/CandidateId = ref
                // XUYÊN service → giữ Guid lỏng (GEN-2), KHÔNG FK.
                e.HasIndex(x => x.SessionId);
                e.HasIndex(x => new { x.CampaignId, x.SessionId });

                // DB13/DB9: required nav (CampaignId NOT NULL) tới Campaign có soft-delete filter →
                // BẮT BUỘC khớp filter, nếu không EF phát PossibleIncorrectRequiredNavigation warning +
                // đọc cờ mồ côi (campaign đã soft-delete). Đọc cờ join campaigns tự ẩn.
                e.HasQueryFilter(x => x.Campaign.DeletedAt == null);

                // DB9: FK nội-service session_flags.campaign_id → campaigns.id. Restrict (bảo vệ cờ gian
                // lận; campaign vốn soft-delete nên cascade không kích hoạt).
                e.HasOne(x => x.Campaign)
                 .WithMany()
                 .HasForeignKey(x => x.CampaignId)
                 .OnDelete(DeleteBehavior.Restrict);
            });

            // ── OutboxMessage (transactional outbox invitation-email — DB2b) ──────
            // Config INLINE (Campaign không có Configurations/ + ApplyConfigurationsFromAssembly). Cột
            // snake_case tự sinh (UseSnakeCaseNamingConvention ở Program.cs). Payload jsonb có ĐIỀU KIỆN
            // IsNpgsql (khớp precedent campaigns.required_skills) — SQLite test không set jsonb.
            modelBuilder.Entity<OutboxMessage>(e =>
            {
                e.ToTable("outbox_messages");
                e.HasKey(x => x.Id);

                e.Property(x => x.Type).HasMaxLength(64).IsRequired();
                e.Property(x => x.Payload).IsRequired();
                if (Database.IsNpgsql())
                    e.Property(x => x.Payload).HasColumnType("jsonb");

                e.Property(x => x.InvitationId).IsRequired();
                e.Property(x => x.CampaignId).IsRequired();
                e.Property(x => x.OccurredAt).IsRequired();
                e.Property(x => x.PublishedAt);
                e.Property(x => x.Attempts).IsRequired();

                // Dispatcher quét row chưa gửi (published_at IS NULL) → partial index chỉ index row còn tồn
                // đọng. Postgres + SQLite (>=3.8) đều hỗ trợ partial index; filter dùng tên cột snake_case
                // (khớp UseSnakeCaseNamingConvention — test cũng phải bật snake_case, DB2 precedent).
                e.HasIndex(x => x.PublishedAt)
                 .HasFilter("published_at IS NULL");
            });
        }
    }
}
