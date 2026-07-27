using System.Text.Json;
using Isas.InterviewService.Entities;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.ChangeTracking;
using Microsoft.EntityFrameworkCore.Metadata.Builders;
using Microsoft.EntityFrameworkCore.Storage.ValueConversion;

namespace Isas.InterviewService.Configurations;

public class RepoAnalysisConfiguration : IEntityTypeConfiguration<RepoAnalysis>
{
    private static readonly JsonSerializerOptions Json = new(JsonSerializerDefaults.Web);
    public void Configure(EntityTypeBuilder<RepoAnalysis> e)
    {
        e.HasKey(x => x.Id);
        e.Property(x => x.RepoUrl).HasColumnType("text").IsRequired();
        e.Property(x => x.RepoOwner).HasMaxLength(39).IsRequired();
        e.Property(x => x.RepoName).HasMaxLength(100).IsRequired();
        e.Property(x => x.DefaultBranch).HasMaxLength(255).IsRequired();
        e.Property(x => x.JobCategory).HasConversion<string>().HasMaxLength(8).IsRequired();
        e.Property(x => x.Summary).HasColumnType("text").IsRequired();
        var list = new ValueConverter<List<string>, string>(v => JsonSerializer.Serialize(v, Json), v => JsonSerializer.Deserialize<List<string>>(v, Json) ?? new List<string>());
        var comparer = new ValueComparer<List<string>>((a,b) => (a ?? new List<string>()).SequenceEqual(b ?? new List<string>()), v => v.Aggregate(0, (h,s) => HashCode.Combine(h,s.GetHashCode())), v => v.ToList());
        foreach (var name in new[] { nameof(RepoAnalysis.TechStack), nameof(RepoAnalysis.Strengths), nameof(RepoAnalysis.Weaknesses), nameof(RepoAnalysis.Suggestions), nameof(RepoAnalysis.InterviewTalkingPoints) })
        { var p = e.Property<List<string>>(name); p.HasConversion(list); p.Metadata.SetValueComparer(comparer); p.HasColumnType("jsonb").IsRequired(); }
        var languages = e.Property(x => x.Languages);
        languages.HasConversion(v => JsonSerializer.Serialize(v, Json), v => JsonSerializer.Deserialize<Dictionary<string,long>>(v, Json) ?? new Dictionary<string,long>());
        languages.Metadata.SetValueComparer(new ValueComparer<Dictionary<string,long>>(
            (a, b) => (a ?? new Dictionary<string,long>()).OrderBy(x => x.Key).SequenceEqual((b ?? new Dictionary<string,long>()).OrderBy(x => x.Key)),
            v => v.Aggregate(0, (h, x) => HashCode.Combine(h, x.Key.GetHashCode(), x.Value)),
            v => v.ToDictionary()));
        languages.HasColumnType("jsonb").IsRequired();
        e.Property(x => x.JdMatch).HasConversion(v => v == null ? null : JsonSerializer.Serialize(v, Json), v => v == null ? null : JsonSerializer.Deserialize<CvJdMatch>(v, Json)).HasColumnType("jsonb");
        e.HasIndex(x => x.CandidateId);
    }
}
