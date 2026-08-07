using System.Text.Json;
using Isas.InterviewService.Entities;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.ChangeTracking;
using Microsoft.EntityFrameworkCore.Metadata.Builders;
using Microsoft.EntityFrameworkCore.Storage.ValueConversion;

namespace Isas.InterviewService.Configurations;

public class RubricCriterionConfiguration : IEntityTypeConfiguration<RubricCriterion>
{
    public void Configure(EntityTypeBuilder<RubricCriterion> e)
    {
        // DB19 — đúng-1-owner: cấm ĐỒNG THỜI campaign_id (B2B) và candidate_id (B2C, BC16).
        // 3 trạng thái loại trừ hợp lệ: campaign-only · candidate-only · both-null (seed mặc định BC11).
        // Không đường code nào set cả 2 (RubricLibraryService=candidate-only · PracticeService=campaign-only
        // · seed=both-null) → CHECK chỉ chặn dữ liệu bẩn, không phá luồng hiện có.
        e.ToTable("rubric_criteria", t =>
        {
            t.HasCheckConstraint(
                "ck_rubric_criteria_single_owner",
                "campaign_id IS NULL OR candidate_id IS NULL");

            // DB15 — weight ∈ (0,1]: khớp code (RubricLibraryService chuẩn hoá Σweight=1 nên mỗi tiêu
            // chí >0; seed BC11 mỗi tiêu chí ≤1). Chặn dữ liệu bẩn (weight ≤0 hoặc >1) ở tầng DB.
            t.HasCheckConstraint(
                "ck_rubric_criteria_weight_range",
                "weight > 0 AND weight <= 1");
            t.HasCheckConstraint(
                "ck_rubric_criteria_language",
                "language IN ('vi', 'en')");
        });

        e.HasKey(x => x.Id);

        e.Property(x => x.Name).HasMaxLength(128).IsRequired();
        e.Property(x => x.Weight).HasColumnType("numeric(5,4)").IsRequired();
        e.Property(x => x.MaxScore).IsRequired();
        e.Property(x => x.IsActive).IsRequired();

        e.Property(x => x.JobCategory)
            .HasConversion<string>()
            .HasMaxLength(8)
            .IsRequired();
        e.Property(x => x.Language).HasColumnType("text").IsRequired().HasDefaultValue("vi");

        e.Property(x => x.Version).IsRequired();

        e.HasIndex(x => new { x.JobCategory, x.Version, x.IsActive });

        // B2B: đọc/materialize tiêu chí theo campaign. Non-unique, nullable.
        e.HasIndex(x => x.CampaignId);

        // BC16: tra rubric riêng của candidate theo nghề (resolve ưu-tiên-riêng-else-mặc-định).
        // Non-unique, nullable (null = seed mặc định dùng chung).
        e.HasIndex(x => new { x.CandidateId, x.JobCategory, x.Language, x.IsActive });

        e.HasMany(x => x.Levels)
            .WithOne(l => l.Criterion)
            .HasForeignKey(l => l.CriterionId)
            .OnDelete(DeleteBehavior.Cascade);
    }
}

public class RubricLevelConfiguration : IEntityTypeConfiguration<RubricLevel>
{
    private static readonly JsonSerializerOptions Json = new(JsonSerializerDefaults.Web);

    public void Configure(EntityTypeBuilder<RubricLevel> e)
    {
        e.HasKey(x => x.Id);

        e.Property(x => x.Score).IsRequired();
        e.Property(x => x.Descriptor).IsRequired();

        e.HasIndex(x => new { x.CriterionId, x.Score }).IsUnique();

        // DB15 — anchor (câu trả lời mẫu neo mức) gộp thành jsonb string[] trên chính rubric_levels
        // (thay bảng rubric_anchors 1-n). Non-null; converter → JSON string nên SQLite (test) serialize
        // ra text == Postgres jsonb. Mẫu giống RoadmapMilestone.FocusCriteria (converter + comparer).
        var listConverter = new ValueConverter<List<string>, string>(
            v => JsonSerializer.Serialize(v, Json),
            v => JsonSerializer.Deserialize<List<string>>(v, Json) ?? new List<string>());

        var listComparer = new ValueComparer<List<string>>(
            (a, b) => (a ?? new List<string>()).SequenceEqual(b ?? new List<string>()),
            v => v == null ? 0 : v.Aggregate(0, (h, s) => HashCode.Combine(h, s.GetHashCode())),
            v => v.ToList());

        var examples = e.Property(x => x.ExampleAnswers);
        examples.HasConversion(listConverter);
        examples.Metadata.SetValueComparer(listComparer);
        examples.HasColumnType("jsonb");
        examples.IsRequired();
    }
}
