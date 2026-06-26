using Isas.InterviewService.Entities;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;

namespace Isas.InterviewService.Configurations;

public class PracticeSessionConfiguration : IEntityTypeConfiguration<PracticeSession>
{
    public void Configure(EntityTypeBuilder<PracticeSession> e)
    {
        e.HasKey(x => x.Id);

        e.Property(x => x.CandidateId).IsRequired();
        // CvId, JdId giờ optional — KHÔNG IsRequired.
        // (Guid? trong entity đã đủ báo nullable; bỏ IsRequired cho rõ ý.)

        e.Property(x => x.Status)
            .HasConversion<string>()
            .HasMaxLength(32)
            .IsRequired();

        e.Property(x => x.JobCategory)
            .HasConversion<string>()
            .HasMaxLength(8)
            .IsRequired();

        e.Property(x => x.CreatedAt).IsRequired();

        e.HasIndex(x => x.CandidateId);

        // B2B: lookup session theo campaign (S3/S4). Non-unique, nullable.
        e.HasIndex(x => x.CampaignId);

        e.HasMany(x => x.Questions)
            .WithOne(q => q.Session)
            .HasForeignKey(q => q.SessionId)
            .OnDelete(DeleteBehavior.Cascade);

        e.HasMany(x => x.Answers)
            .WithOne(a => a.Session)
            .HasForeignKey(a => a.SessionId)
            .OnDelete(DeleteBehavior.Cascade);

        // FK tới FileRecord (CV + JD) — giờ OPTIONAL (IsRequired(false)).
        // Restrict: không cho xóa file khi còn session tham chiếu.
        e.HasOne<FileRecord>()
            .WithMany()
            .HasForeignKey(x => x.CvId)
            .IsRequired(false)
            .OnDelete(DeleteBehavior.Restrict);

        e.HasOne<FileRecord>()
            .WithMany()
            .HasForeignKey(x => x.JdId)
            .IsRequired(false)
            .OnDelete(DeleteBehavior.Restrict);
    }
}
public class PracticeQuestionConfiguration : IEntityTypeConfiguration<PracticeQuestion>
{
    public void Configure(EntityTypeBuilder<PracticeQuestion> e)
    {
        e.HasKey(x => x.Id);

        e.Property(x => x.Content).IsRequired();
        e.Property(x => x.OrderNo).IsRequired();
        e.Property(x => x.TimeLimitSec).IsRequired();
        e.Property(x => x.CreatedAt).IsRequired();

        e.HasIndex(x => new { x.SessionId, x.OrderNo }).IsUnique();
    }
}

public class PracticeAnswerConfiguration : IEntityTypeConfiguration<PracticeAnswer>
{
    public void Configure(EntityTypeBuilder<PracticeAnswer> e)
    {
        e.HasKey(x => x.Id);

        e.Property(x => x.AudioObjectKey).HasMaxLength(512);

        e.Property(x => x.Status)
            .HasConversion<string>()
            .HasMaxLength(32)
            .IsRequired();

        e.Property(x => x.CreatedAt).IsRequired();

        // Business rule: tối đa 1 answer mỗi câu hỏi (chống trùng khi retry upload)
        e.HasIndex(x => new { x.SessionId, x.QuestionId }).IsUnique();

        // Restrict (KHÔNG Cascade): tránh multiple cascade paths.
        // Answer vẫn bị xóa khi session xóa qua đường session -> answers ở trên.
        e.HasOne(x => x.Question)
            .WithOne(q => q.Answer)
            .HasForeignKey<PracticeAnswer>(x => x.QuestionId)
            .OnDelete(DeleteBehavior.Restrict);

        e.HasMany(x => x.Scores)
            .WithOne(s => s.Answer)
            .HasForeignKey(s => s.AnswerId)
            .OnDelete(DeleteBehavior.Cascade);
    }
}

public class AnswerScoreConfiguration : IEntityTypeConfiguration<AnswerScore>
{
    public void Configure(EntityTypeBuilder<AnswerScore> e)
    {
        e.HasKey(x => x.Id);

        e.Property(x => x.Score).HasColumnType("numeric(5,2)").IsRequired();
        e.Property(x => x.AttemptNo).IsRequired();
        e.Property(x => x.RubricVersion).IsRequired();
        e.Property(x => x.CreatedAt).IsRequired();

        // Một tiêu chí chấm cho một answer ở một lần chấm là duy nhất
        e.HasIndex(x => new { x.AnswerId, x.CriterionId, x.AttemptNo }).IsUnique();

        e.HasOne(x => x.Criterion)
            .WithMany()
            .HasForeignKey(x => x.CriterionId)
            .OnDelete(DeleteBehavior.Restrict);
    }
}