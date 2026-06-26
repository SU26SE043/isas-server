using Isas.InterviewService.Entities;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;

namespace Isas.InterviewService.Configurations;

public class RubricCriterionConfiguration : IEntityTypeConfiguration<RubricCriterion>
{
    public void Configure(EntityTypeBuilder<RubricCriterion> e)
    {
        e.HasKey(x => x.Id);

        e.Property(x => x.Name).HasMaxLength(128).IsRequired();
        e.Property(x => x.Weight).HasColumnType("numeric(5,4)").IsRequired();
        e.Property(x => x.MaxScore).IsRequired();
        e.Property(x => x.IsActive).IsRequired();

        e.Property(x => x.JobCategory)
            .HasConversion<string>()
            .HasMaxLength(8)
            .IsRequired();

        e.Property(x => x.Version).IsRequired();

        e.HasIndex(x => new { x.JobCategory, x.Version, x.IsActive });

        // B2B: đọc/materialize tiêu chí theo campaign. Non-unique, nullable.
        e.HasIndex(x => x.CampaignId);

        e.HasMany(x => x.Levels)
            .WithOne(l => l.Criterion)
            .HasForeignKey(l => l.CriterionId)
            .OnDelete(DeleteBehavior.Cascade);
    }
}

public class RubricLevelConfiguration : IEntityTypeConfiguration<RubricLevel>
{
    public void Configure(EntityTypeBuilder<RubricLevel> e)
    {
        e.HasKey(x => x.Id);

        e.Property(x => x.Score).IsRequired();
        e.Property(x => x.Descriptor).IsRequired();

        e.HasIndex(x => new { x.CriterionId, x.Score }).IsUnique();

        e.HasMany(x => x.Anchors)
            .WithOne(a => a.Level)
            .HasForeignKey(a => a.LevelId)
            .OnDelete(DeleteBehavior.Cascade);
    }
}

public class RubricAnchorConfiguration : IEntityTypeConfiguration<RubricAnchor>
{
    public void Configure(EntityTypeBuilder<RubricAnchor> e)
    {
        e.HasKey(x => x.Id);

        e.Property(x => x.ExampleAnswer).IsRequired();
    }
}