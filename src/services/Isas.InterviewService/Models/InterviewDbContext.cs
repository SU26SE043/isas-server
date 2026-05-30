using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Storage.ValueConversion;
using System.Text.Json;

namespace Isas.InterviewService.Models
{
    public class InterviewDbContext : DbContext
    {
        public InterviewDbContext(DbContextOptions<InterviewDbContext> options)
            : base(options)
        {
        }

        public DbSet<FileRecord> Files => Set<FileRecord>();

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
        }
    }
}
