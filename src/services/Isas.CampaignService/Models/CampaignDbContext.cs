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
        public DbSet<CampaignCandidate> CampaignCandidates => Set<CampaignCandidate>();          // C13: sàng CV
        public DbSet<CandidateCriterionScore> CandidateCriterionScores => Set<CandidateCriterionScore>();

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

                e.HasOne(x => x.Campaign)
                 .WithMany(x => x.Invitations)
                 .HasForeignKey(x => x.CampaignId)
                 .OnDelete(DeleteBehavior.Cascade);
            });

            // ── CampaignRanking (read-model B2B — E4/D10, event SessionScored) ─────
            modelBuilder.Entity<CampaignRanking>(e =>
            {
                e.ToTable("campaign_rankings");
                e.HasKey(x => x.Id);
                e.Property(x => x.Id).HasDefaultValueSql("gen_random_uuid()");
                e.Property(x => x.TotalScore).HasColumnType("numeric(5,2)");
                e.Property(x => x.UpdatedAt).HasDefaultValueSql("now()");

                // Idempotent upsert theo session_id: event tới 2 lần vẫn 1 row.
                e.HasIndex(x => x.SessionId).IsUnique();
                e.HasIndex(x => new { x.CampaignId, x.TotalScore });
            });

            // ── CampaignCandidate (sàng CV B2B — C13/D18) ──────────────────────
            modelBuilder.Entity<CampaignCandidate>(e =>
            {
                e.ToTable("campaign_candidates");
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
                e.HasIndex(x => x.CandidateId);

                e.HasOne(x => x.Campaign)
                 .WithMany(x => x.Candidates)
                 .HasForeignKey(x => x.CampaignId)
                 .OnDelete(DeleteBehavior.Cascade);
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

                e.HasOne(x => x.Candidate)
                 .WithMany(x => x.CriterionScores)
                 .HasForeignKey(x => x.CandidateId)
                 .OnDelete(DeleteBehavior.Cascade);

                // Restrict: chặn xoá tiêu chí còn điểm tham chiếu (TÁI DÙNG rubric campaign_criteria).
                e.HasOne(x => x.Criterion)
                 .WithMany()
                 .HasForeignKey(x => x.CriterionId)
                 .OnDelete(DeleteBehavior.Restrict);
            });
        }
    }
}
