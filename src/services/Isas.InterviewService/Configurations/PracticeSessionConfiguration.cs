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

        // DB14 — audit updated_at: default now() ở DB (Postgres); C# init ở entity đảm nhận insert
        // (SQLite/EnsureCreated không có now()). Stamp tự động khi Modified qua SaveChanges override.
        e.Property(x => x.UpdatedAt).HasDefaultValueSql("now()");

        // BC9 — điểm tổng buổi B2C (nullable; set khi Scored).
        e.Property(x => x.OverallScore).HasColumnType("numeric(5,2)");

        // BC10 — nhận xét chung buổi (AI sinh, nullable; set best-effort khi Scored). text (không giới hạn).
        e.Property(x => x.OverallComment).HasColumnType("text");

        e.HasIndex(x => x.CandidateId);

        // B2B: lookup session theo campaign (S3/S4). Non-unique, nullable.
        e.HasIndex(x => x.CampaignId);

        // DB5 — hỗ trợ SessionAbandonSweeper (quét mỗi 2', trước đây seq-scan cả bảng).
        // (1) B2B quá hạn nhận bài (ScanExpiredB2BAsync): status=InProgress + deadline != null + deadline < now.
        // Partial index trên deadline (chỉ row CÓ deadline = B2B) → sweeper không quét row B2C (deadline null).
        e.HasIndex(x => x.Deadline)
            .HasDatabaseName("ix_practice_sessions_deadline")
            .HasFilter("deadline IS NOT NULL");

        // (2) B2C không hoạt động (ScanInactiveB2CAsync): status Ready|InProgress + deadline == null +
        // campaign_id == null + created_at < cutoff. Partial index trên created_at (chỉ row B2C không-deadline)
        // → tránh seq-scan; range-scan created_at trong tập B2C. Filter snake_case (SQLite test dùng snake_case).
        e.HasIndex(x => x.CreatedAt)
            .HasDatabaseName("ix_practice_sessions_b2c_active")
            .HasFilter("campaign_id IS NULL AND deadline IS NULL");

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

        // Phỏng vấn THÍCH ỨNG — Kind lưu string (GEN-2). Rows cũ backfill 'Seed' (migration defaultValue).
        e.Property(x => x.Kind)
            .HasConversion<string>()
            .HasMaxLength(16)
            .IsRequired();

        e.HasIndex(x => new { x.SessionId, x.OrderNo }).IsUnique();

        // Phỏng vấn THÍCH ỨNG — 1 answer sinh TỐI ĐA 1 câu kế: unique filtered index trên
        // generated_from_answer_id (chỉ row thích ứng có giá trị; seed = null không tính). Là backstop
        // đồng thời cho re-upload / double-POST cùng frontier answer (insert thứ 2 vỡ unique). Filter
        // snake_case vì SQLite test dùng UseSnakeCaseNamingConvention (precedent DB5/DB19).
        e.HasIndex(x => x.GeneratedFromAnswerId)
            .IsUnique()
            .HasFilter("generated_from_answer_id IS NOT NULL");
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

        // DB15 — bỏ UNIQUE(session_id, question_id) THỪA: quan hệ 1-1 với câu hỏi (HasForeignKey
        // question_id ở dưới) đã sinh UNIQUE ix_practice_answers_question_id; question_id là duy nhất
        // toàn cục ⇒ "tối đa 1 answer/câu" đã được đảm bảo. GIỮ cột dẫn session_id bằng index NON-unique
        // để các truy vấn EXISTS theo session_id (SessionAbandonSweeper quét mỗi 2', PracticeService
        // kiểm ≥1 answer) không seq-scan (không có index session_id đứng riêng nào khác).
        e.HasIndex(x => x.SessionId);

        // DB5 — hỗ trợ StuckAnswerRepublisher (quét mỗi 2', trước đây seq-scan cả bảng). Lọc theo
        // Status ∈ {Uploaded,Scoring} rồi so LastScoringPublishedAt (null/aged). Composite non-partial
        // (status, last_scoring_published_at) → leading col status thu hẹp tập, col 2 phục vụ so mốc thời gian.
        e.HasIndex(x => new { x.Status, x.LastScoringPublishedAt })
            .HasDatabaseName("ix_practice_answers_status_lsp");

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

// BC9 — breakdown điểm mỗi tiêu chí của buổi luyện B2C (ghi khi session Scored).
public class SessionCriterionScoreConfiguration : IEntityTypeConfiguration<SessionCriterionScore>
{
    public void Configure(EntityTypeBuilder<SessionCriterionScore> e)
    {
        e.HasKey(x => x.Id);

        e.Property(x => x.CriterionName).HasMaxLength(128).IsRequired();
        e.Property(x => x.AverageScore).HasColumnType("numeric(5,2)").IsRequired();
        e.Property(x => x.MaxScore).IsRequired();
        e.Property(x => x.Percentage).HasColumnType("numeric(5,2)").IsRequired();
        e.Property(x => x.Weight).HasColumnType("numeric(5,4)").IsRequired();
        e.Property(x => x.NeedsImprovement).IsRequired();
        e.Property(x => x.CreatedAt).IsRequired();

        // 1 row / (buổi, tiêu chí) — idempotent theo session (xoá + ghi lại khi tính lại).
        e.HasIndex(x => new { x.SessionId, x.CriterionId }).IsUnique();

        e.HasOne(x => x.Session)
            .WithMany(s => s.CriterionScores)
            .HasForeignKey(x => x.SessionId)
            .OnDelete(DeleteBehavior.Cascade);

        // criterion_id → rubric_criteria (Restrict): giữ điểm lịch sử, chặn xoá tiêu chí đang tham chiếu.
        e.HasOne<RubricCriterion>()
            .WithMany()
            .HasForeignKey(x => x.CriterionId)
            .OnDelete(DeleteBehavior.Restrict);
    }
}