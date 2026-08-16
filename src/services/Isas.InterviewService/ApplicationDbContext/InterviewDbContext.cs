using Isas.InterviewService.Data;
using Isas.InterviewService.Entities;
using Isas.InterviewService.Models;
using Microsoft.EntityFrameworkCore;

namespace Isas.InterviewService.ApplicationDbContext
{
    public class InterviewDbContext(DbContextOptions<InterviewDbContext> options) : DbContext(options)
    {
        public DbSet<PracticeSession> PracticeSessions => Set<PracticeSession>();
        public DbSet<PracticeQuestion> PracticeQuestions => Set<PracticeQuestion>();
        public DbSet<PracticeAnswer> PracticeAnswers => Set<PracticeAnswer>();
        public DbSet<AnswerScore> AnswerScores => Set<AnswerScore>();
        public DbSet<SessionCriterionScore> SessionCriterionScores => Set<SessionCriterionScore>();  // BC9
        public DbSet<SessionCriterionEvidence> SessionCriterionEvidence => Set<SessionCriterionEvidence>();
        public DbSet<RubricCriterion> RubricCriteria => Set<RubricCriterion>();
        public DbSet<AdminRubricPreviewRun> AdminRubricPreviewRuns => Set<AdminRubricPreviewRun>();
        public DbSet<RubricLevel> RubricLevels => Set<RubricLevel>();
        // DB15 — rubric_anchors gộp thành cột jsonb rubric_levels.example_answers (bỏ bảng/entity).

        public DbSet<FileRecord> FileRecords => Set<FileRecord>();

        public DbSet<CvAnalysis> CvAnalyses => Set<CvAnalysis>();   // BC7
        public DbSet<RepoAnalysis> RepoAnalyses => Set<RepoAnalysis>(); // BC18

        public DbSet<Roadmap> Roadmaps => Set<Roadmap>();                          // BC12
        public DbSet<RoadmapMilestone> RoadmapMilestones => Set<RoadmapMilestone>();  // BC12
        public DbSet<RoadmapLesson> RoadmapLessons => Set<RoadmapLesson>();        // BC12

        public DbSet<OutboxMessage> OutboxMessages => Set<OutboxMessage>();        // DB2 — transactional outbox

        public DbSet<KnowledgeSource> KnowledgeSources => Set<KnowledgeSource>();  // RAG grounding — metadata nguồn (chunk ở Qdrant)

        public DbSet<PromptTemplate> PromptTemplates => Set<PromptTemplate>();    // F21 — prompt tuỳ biến (FR17)

        // DB14 — đóng dấu updated_at TỰ ĐỘNG cho entity IHasUpdatedAt bị SỬA (Modified). SaveChanges()
        // parameterless của EF gọi xuống overload (bool) này → override 2 overload dưới là đủ mọi đường ghi
        // tracked. LƯU Ý: ExecuteUpdateAsync KHÔNG đi qua SaveChanges → các call flip practice_sessions.status
        // (SessionAbandonSweeper) / overall_comment (SessionScoringNotifier) tự thêm .SetProperty(UpdatedAt).
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

        protected override void OnModelCreating(ModelBuilder b)
        {
            base.OnModelCreating(b);
            b.Entity<PracticeSession>(e =>
            {
                e.Property(s => s.JobCategory)
                    .HasConversion<string>()
                    .HasMaxLength(8)
                    .IsRequired();
            });
            b.ApplyConfigurationsFromAssembly(typeof(InterviewDbContext).Assembly);

            // CHỈ áp cho Npgsql (SQLite test EnsureCreated bỏ qua) — 2 điều:
            if (Database.IsNpgsql())
            {
                // DB10 — optimistic concurrency qua system column xmin (Postgres). KHÔNG thêm DDL
                // (cột hệ thống có sẵn); SQLite không có xmin nên gate provider để EnsureCreated không
                // dựng token này. Chặn lost-update trên practice_sessions (đóng buổi ↔ sweeper cùng lúc).
                // Npgsql 10 bỏ helper UseXminAsConcurrencyToken() → map shadow property "xmin" tay
                // (uint → cột hệ thống xid, ValueGeneratedOnAddOrUpdate + IsConcurrencyToken) như doc EF.PG.
                b.Entity<PracticeSession>().Property<uint>("xmin")
                    .HasColumnName("xmin")
                    .HasColumnType("xid")
                    .ValueGeneratedOnAddOrUpdate()
                    .IsConcurrencyToken();

                // BC11: seed rubric B2C mặc định (BA/BE/FE) qua HasData → EF sinh InsertData literal
                // trong migration, apply qua pipeline/tay (KHÔNG auto-migrate Neon, KHÔNG seed runtime).
                // Test SQLite giữ rubric "controlled" như cũ (không seed sẵn) để không phá E1/E2/E8;
                // test BC11 tự nạp seed khi cần.
                b.Entity<RubricCriterion>().HasData(B2CRubricSeed.Build());
            }
        }
    }
}
