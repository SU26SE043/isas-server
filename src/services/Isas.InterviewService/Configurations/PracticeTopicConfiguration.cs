using Isas.InterviewService.Entities;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;

namespace Isas.InterviewService.Configurations;

public class PracticeTopicConfiguration : IEntityTypeConfiguration<PracticeTopic>
{
    public void Configure(EntityTypeBuilder<PracticeTopic> e)
    {
        e.ToTable("practice_topics", t =>
        {
            // Tập đóng, khớp Seniority enum + ck_practice_sessions_seniority (mẫu dùng chung toàn repo).
            t.HasCheckConstraint(
                "ck_practice_topics_seniority",
                "seniority IN ('Fresher', 'Junior', 'Middle', 'Senior')");
        });

        e.HasKey(x => x.Id);

        e.Property(x => x.TopicKey).HasMaxLength(64).IsRequired();

        e.Property(x => x.JobCategory)
            .HasConversion<string>()
            .HasMaxLength(8)
            .IsRequired();

        e.Property(x => x.Seniority).HasMaxLength(16).IsRequired();

        e.Property(x => x.Language).HasColumnType("text").IsRequired().HasDefaultValue("vi");

        e.Property(x => x.Label).HasColumnType("text").IsRequired();

        e.Property(x => x.CriterionName).HasColumnType("text");

        e.Property(x => x.DisplayOrder).IsRequired();
        e.Property(x => x.IsActive).IsRequired();
        e.Property(x => x.Version).IsRequired();

        // Soft-version: bump version đụng UNIQUE mới, KHÔNG đụng bản cũ (mẫu rubric_criteria).
        e.HasIndex(x => new { x.TopicKey, x.Language, x.Version })
            .IsUnique()
            .HasDatabaseName("ux_practice_topics_key_language_version");

        // Đọc danh sách chọn lúc tạo buổi: lọc theo (nghề, cấp độ, ngôn ngữ, đang bật).
        e.HasIndex(x => new { x.JobCategory, x.Seniority, x.Language, x.IsActive })
            .HasDatabaseName("ix_practice_topics_lookup");
    }
}
