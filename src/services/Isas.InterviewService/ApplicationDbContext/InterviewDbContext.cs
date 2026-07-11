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
        public DbSet<RubricCriterion> RubricCriteria => Set<RubricCriterion>();
        public DbSet<RubricLevel> RubricLevels => Set<RubricLevel>();
        public DbSet<RubricAnchor> RubricAnchors => Set<RubricAnchor>();
        
        public DbSet<FileRecord> FileRecords => Set<FileRecord>();
 
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

            // BC11: seed rubric B2C mặc định (BA/BE/FE) qua HasData → EF sinh InsertData literal
            // trong migration, apply qua pipeline/tay (KHÔNG auto-migrate Neon, KHÔNG seed runtime).
            // CHỈ áp cho Npgsql: test SQLite dùng EnsureCreated giữ rubric "controlled" như cũ
            // (không seed sẵn) để không phá test E1/E2/E8 hiện có; test BC11 tự nạp seed khi cần.
            if (Database.IsNpgsql())
                b.Entity<RubricCriterion>().HasData(B2CRubricSeed.Build());
        }
    }
}