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
        }
    }
}