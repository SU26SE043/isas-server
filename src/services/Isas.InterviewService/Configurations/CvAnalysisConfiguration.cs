using System.Text.Json;
using Isas.InterviewService.Entities;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.ChangeTracking;
using Microsoft.EntityFrameworkCore.Metadata.Builders;
using Microsoft.EntityFrameworkCore.Storage.ValueConversion;

namespace Isas.InterviewService.Configurations;

// BC7 — bảng cv_analyses. Cột snake_case tự sinh (UseSnakeCaseNamingConvention).
// strengths/weaknesses/suggestions/jd_match lưu jsonb (value converter → JSON string).
public class CvAnalysisConfiguration : IEntityTypeConfiguration<CvAnalysis>
{
    private static readonly JsonSerializerOptions Json = new(JsonSerializerDefaults.Web);

    public void Configure(EntityTypeBuilder<CvAnalysis> e)
    {
        e.HasKey(x => x.Id);

        e.Property(x => x.CandidateId).IsRequired();
        e.Property(x => x.CvId).IsRequired();
        e.Property(x => x.JdId);

        e.Property(x => x.JobCategory)
            .HasConversion<string>()
            .HasMaxLength(8)
            .IsRequired();

        e.Property(x => x.Summary).HasColumnType("text").IsRequired();

        var listConverter = new ValueConverter<List<string>, string>(
            v => JsonSerializer.Serialize(v, Json),
            v => JsonSerializer.Deserialize<List<string>>(v, Json) ?? new List<string>());

        var listComparer = new ValueComparer<List<string>>(
            (a, b) => (a ?? new List<string>()).SequenceEqual(b ?? new List<string>()),
            v => v == null ? 0 : v.Aggregate(0, (h, s) => HashCode.Combine(h, s.GetHashCode())),
            v => v.ToList());

        foreach (var prop in new[] { nameof(CvAnalysis.Strengths), nameof(CvAnalysis.Weaknesses), nameof(CvAnalysis.Suggestions) })
        {
            var pb = e.Property<List<string>>(prop);
            pb.HasConversion(listConverter);
            pb.Metadata.SetValueComparer(listComparer);
            pb.HasColumnType("jsonb");
            pb.IsRequired();
        }

        e.Property(x => x.JdMatch)
            .HasConversion(
                v => v == null ? null : JsonSerializer.Serialize(v, Json),
                v => v == null ? null : JsonSerializer.Deserialize<CvJdMatch>(v, Json))
            .HasColumnType("jsonb");

        e.Property(x => x.RequirementMatches)
            .HasConversion(
                v => v == null ? null : JsonSerializer.Serialize(v, Json),
                v => v == null ? null : JsonSerializer.Deserialize<List<CvRequirementMatch>>(v, Json))
            .HasColumnType("jsonb");
        e.Property(x => x.RequirementMatches).Metadata.SetValueComparer(JsonListComparer<CvRequirementMatch>());

        e.Property(x => x.CvSections)
            .HasConversion(
                v => v == null ? null : JsonSerializer.Serialize(v, Json),
                v => v == null ? null : JsonSerializer.Deserialize<List<CvSectionAnchor>>(v, Json))
            .HasColumnType("jsonb");
        e.Property(x => x.CvSections).Metadata.SetValueComparer(JsonListComparer<CvSectionAnchor>());

        e.Property(x => x.Citations)
            .HasConversion(
                v => v == null ? null : JsonSerializer.Serialize(v, Json),
                v => v == null ? null : JsonSerializer.Deserialize<List<CvAnalysisCitation>>(v, Json))
            .HasColumnType("jsonb");
        e.Property(x => x.Citations).Metadata.SetValueComparer(JsonListComparer<CvAnalysisCitation>());

        e.Property(x => x.CreatedAt).IsRequired();

        // Lịch sử phân tích CV của 1 user (GET /cv-analysis).
        e.HasIndex(x => x.CandidateId);
    }

    private static ValueComparer<List<T>?> JsonListComparer<T>()
        => new(
            (a, b) => JsonSerializer.Serialize(a, Json) == JsonSerializer.Serialize(b, Json),
            value => JsonSerializer.Serialize(value, Json).GetHashCode(),
            value => value == null ? null : value.ToList());
}
