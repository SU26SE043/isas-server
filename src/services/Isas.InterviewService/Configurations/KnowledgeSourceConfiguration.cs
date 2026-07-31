using Isas.InterviewService.Entities;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;

namespace Isas.InterviewService.Configurations;

// RAG grounding — metadata nguồn tri thức (chunk vector ở Qdrant, KHÔNG ở đây). Cột snake_case tự sinh.
// Enum lưu string (GEN-2). Không jsonb → SQLite test EnsureCreated qua sạch.
public class KnowledgeSourceConfiguration : IEntityTypeConfiguration<KnowledgeSource>
{
    public void Configure(EntityTypeBuilder<KnowledgeSource> e)
    {
        e.ToTable("knowledge_sources");
        e.HasKey(x => x.Id);

        e.Property(x => x.Title).HasMaxLength(256).IsRequired();

        // Nullable — nguồn "chung mọi nghề" không lọc được theo jobCategory khi retrieve.
        e.Property(x => x.JobCategory)
            .HasConversion<string>()
            .HasMaxLength(8);

        e.Property(x => x.SourceType)
            .HasConversion<string>()
            .HasMaxLength(16)
            .IsRequired();

        e.Property(x => x.SourceRef).HasColumnType("text");
        e.Property(x => x.RawContent).HasColumnType("text");   // nội dung gốc để reindex (nullable)
        e.Property(x => x.Reputation).HasMaxLength(64);

        e.Property(x => x.Status)
            .HasConversion<string>()
            .HasMaxLength(16)
            .IsRequired();

        e.Property(x => x.ChunkCount).IsRequired();
        e.Property(x => x.CreatedAt).IsRequired();

        // Admin list keyset (created_at DESC, id DESC) — mẫu DB8/DB31.
        e.HasIndex(x => new { x.CreatedAt, x.Id })
            .HasDatabaseName("ix_knowledge_sources_created")
            .IsDescending(true, true);

        // Lọc list theo nghề (admin xem theo nghề).
        e.HasIndex(x => x.JobCategory);
    }
}
