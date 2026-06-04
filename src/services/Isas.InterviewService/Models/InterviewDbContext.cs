using Microsoft.EntityFrameworkCore;

namespace Isas.InterviewService.Models
{
    public class InterviewDbContext : DbContext
    {
        public InterviewDbContext(DbContextOptions<InterviewDbContext> options)
            : base(options)
        {
        }

        public DbSet<FileRecord> Files => Set<FileRecord>();
        public DbSet<PracticeSession> PracticeSessions => Set<PracticeSession>();
        public DbSet<PracticeQuestion> PracticeQuestions => Set<PracticeQuestion>();
        public DbSet<PracticeAnswer> PracticeAnswers => Set<PracticeAnswer>();

        protected override void OnModelCreating(ModelBuilder builder)
        {
            
            builder.Entity<FileRecord>(e =>
            {
                e.ToTable("files");
                e.HasKey(f => f.Id);

                e.Property(f => f.Id).HasColumnName("id");
                e.Property(f => f.UserId).HasColumnName("user_id");
                e.Property(f => f.FileType).HasColumnName("file_type").HasMaxLength(20);
                e.Property(f => f.OriginalName).HasColumnName("original_name").HasMaxLength(255);
                e.Property(f => f.StoragePath).HasColumnName("storage_path").HasMaxLength(500);
                e.Property(f => f.StorageBucket).HasColumnName("storage_bucket").HasMaxLength(100);
                e.Property(f => f.MimeType).HasColumnName("mime_type").HasMaxLength(100);
                e.Property(f => f.FileSize).HasColumnName("file_size");
                e.Property(f => f.ParsedText).HasColumnName("parsed_text");
                e.Property(f => f.ParseStatus).HasColumnName("parse_status").HasMaxLength(20).HasDefaultValue("pending");
                e.Property(f => f.CreatedAt).HasColumnName("created_at").HasDefaultValueSql("NOW()");
                e.Property(f => f.UpdatedAt).HasColumnName("updated_at").HasDefaultValueSql("NOW()");
                e.HasIndex(f => f.UserId).HasDatabaseName("idx_files_user");
                e.HasIndex(f => new { f.UserId, f.FileType }).HasDatabaseName("idx_files_user_type");
                e.HasIndex(f => f.ParseStatus).HasDatabaseName("idx_files_parse_status");
            });

            builder.Entity<PracticeSession>(e =>
            {
                e.ToTable("practice_sessions");
                e.HasKey(s => s.Id);

                e.Property(s => s.Id).HasColumnName("id");
                e.Property(s => s.UserId).HasColumnName("user_id");
                e.Property(s => s.JobCategory).HasColumnName("job_category").HasMaxLength(10);
                e.Property(s => s.Status).HasColumnName("status").HasMaxLength(20);
                e.Property(s => s.CvFileId).HasColumnName("cv_file_id");
                e.Property(s => s.JdText).HasColumnName("jd_text");
                e.Property(s => s.TotalScore).HasColumnName("total_score").HasColumnType("numeric(5,2)");
                e.Property(s => s.Feedback).HasColumnName("feedback");
                e.Property(s => s.CreatedAt).HasColumnName("created_at").HasDefaultValueSql("NOW()");
                e.Property(s => s.SubmittedAt).HasColumnName("submitted_at");
                e.Property(s => s.ScoredAt).HasColumnName("scored_at");

                e.HasOne(s => s.CvFile)
                    .WithMany()
                    .HasForeignKey(s => s.CvFileId)
                    .OnDelete(DeleteBehavior.SetNull);

                e.HasIndex(s => s.UserId).HasDatabaseName("idx_practice_user");
                e.HasIndex(s => s.Status).HasDatabaseName("idx_practice_status");
            });

            builder.Entity<PracticeQuestion>(e =>
            {
                e.ToTable("practice_questions");
                e.HasKey(q => q.Id);

                e.Property(q => q.Id).HasColumnName("id");
                e.Property(q => q.SessionId).HasColumnName("session_id");
                e.Property(q => q.OrderIndex).HasColumnName("order_index");
                e.Property(q => q.Content).HasColumnName("content");
                e.Property(q => q.CreatedAt).HasColumnName("created_at").HasDefaultValueSql("NOW()");

                e.HasOne(q => q.Session)
                    .WithMany(s => s.Questions)
                    .HasForeignKey(q => q.SessionId)
                    .OnDelete(DeleteBehavior.Cascade);

                e.HasIndex(q => q.SessionId).HasDatabaseName("idx_practice_q_session");
                e.HasIndex(q => new { q.SessionId, q.OrderIndex }).HasDatabaseName("idx_practice_q_order");
            });

            builder.Entity<PracticeAnswer>(e =>
            {
                e.ToTable("practice_answers");
                e.HasKey(a => a.Id);

                e.Property(a => a.Id).HasColumnName("id");
                e.Property(a => a.QuestionId).HasColumnName("question_id");
                e.Property(a => a.SessionId).HasColumnName("session_id");
                e.Property(a => a.AnswerType).HasColumnName("answer_type").HasMaxLength(10);
                e.Property(a => a.TextContent).HasColumnName("text_content");
                e.Property(a => a.AudioFileId).HasColumnName("audio_file_id");
                e.Property(a => a.Score).HasColumnName("score").HasColumnType("numeric(5,2)");
                e.Property(a => a.Feedback).HasColumnName("feedback");
                e.Property(a => a.CreatedAt).HasColumnName("created_at").HasDefaultValueSql("NOW()");

                e.HasOne(a => a.Question)
                    .WithOne(q => q.Answer)
                    .HasForeignKey<PracticeAnswer>(a => a.QuestionId)
                    .OnDelete(DeleteBehavior.Cascade);

                e.HasOne(a => a.AudioFile)
                    .WithMany()
                    .HasForeignKey(a => a.AudioFileId)
                    .OnDelete(DeleteBehavior.SetNull);

                e.HasIndex(a => a.SessionId).HasDatabaseName("idx_practice_a_session");
                e.HasIndex(a => a.QuestionId).HasDatabaseName("idx_practice_a_question");
            });
        }
    }
}