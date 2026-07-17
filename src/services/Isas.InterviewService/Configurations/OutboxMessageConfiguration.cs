using Isas.InterviewService.Entities;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;

namespace Isas.InterviewService.Configurations;

// DB2 — bảng outbox_messages. Cột snake_case tự sinh (UseSnakeCaseNamingConvention). Payload lưu jsonb
// (khớp precedent cv_analyses/roadmaps trong service này — SQLite test dùng EnsureCreated vẫn qua vì
// cột string mang column-type "jsonb" chỉ ảnh hưởng affinity, không phá CRUD).
public class OutboxMessageConfiguration : IEntityTypeConfiguration<OutboxMessage>
{
    public void Configure(EntityTypeBuilder<OutboxMessage> e)
    {
        e.ToTable("outbox_messages");
        e.HasKey(x => x.Id);

        e.Property(x => x.Type).HasMaxLength(64).IsRequired();
        e.Property(x => x.Payload).HasColumnType("jsonb").IsRequired();
        e.Property(x => x.SessionId).IsRequired();
        e.Property(x => x.OccurredAt).IsRequired();
        e.Property(x => x.PublishedAt);
        e.Property(x => x.Attempts).IsRequired();

        // Dispatcher quét row chưa gửi (published_at IS NULL) → partial index để chỉ index row còn tồn
        // đọng. Postgres + SQLite (>=3.8) đều hỗ trợ partial index với filter này (cột snake_case).
        e.HasIndex(x => x.PublishedAt)
            .HasFilter("published_at IS NULL");
    }
}
