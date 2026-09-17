using Isas.InterviewService.Entities;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;

namespace Isas.InterviewService.Configurations;

public class PracticeFaceImageConfiguration : IEntityTypeConfiguration<PracticeFaceImage>
{
    public void Configure(EntityTypeBuilder<PracticeFaceImage> e)
    {
        e.HasKey(x => x.Id);

        e.Property(x => x.StorageKey).HasMaxLength(512).IsRequired();
        e.HasIndex(x => x.StorageKey).IsUnique();

        e.Property(x => x.CapturedAt).IsRequired();
        e.HasIndex(x => x.CapturedAt);   // job purge quét theo mốc này

        e.HasIndex(x => x.SessionId);
    }
}
