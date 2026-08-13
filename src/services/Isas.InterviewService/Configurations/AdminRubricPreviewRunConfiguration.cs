using Isas.InterviewService.Entities;
using Isas.InterviewService.Enums;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;

namespace Isas.InterviewService.Configurations;

// Bảng admin_rubric_preview_runs. Cột snake_case tự sinh (UseSnakeCaseNamingConvention).
public class AdminRubricPreviewRunConfiguration : IEntityTypeConfiguration<AdminRubricPreviewRun>
{
    public void Configure(EntityTypeBuilder<AdminRubricPreviewRun> e)
    {
        e.ToTable("admin_rubric_preview_runs", t =>
        {
            t.HasCheckConstraint(
                "ck_admin_rubric_preview_runs_status",
                "status IN ('Running', 'Succeeded', 'Failed')");
            t.HasCheckConstraint(
                "ck_admin_rubric_preview_runs_language",
                "language IN ('vi', 'en')");
        });

        e.HasKey(x => x.Id);

        e.Property(x => x.JobCategory).HasConversion<string>().HasMaxLength(8).IsRequired();
        e.Property(x => x.Language).HasColumnType("text").IsRequired().HasDefaultValue("vi");
        e.Property(x => x.RubricVersion).IsRequired();
        e.Property(x => x.QuestionText).IsRequired();

        // Enum lưu string (GEN-2). Độ dài chừa gấp đôi chuỗi dài nhất ('Succeeded' = 9) — bài học S11:
        // varchar(16) gặp enum 19 ký tự làm VỠ mọi lượt ghi trên Postgres trong khi SQLite không
        // enforce độ dài nên test vẫn xanh 100%.
        e.Property(x => x.Status).HasConversion<string>().HasMaxLength(24).IsRequired();

        e.Property(x => x.RubricSnapshot).IsRequired();
        e.Property(x => x.RubricFingerprint).HasMaxLength(64).IsRequired();
        e.Property(x => x.ErrorReason).HasMaxLength(500);

        // Đọc lịch sử theo phạm vi, mới nhất trước.
        e.HasIndex(x => new { x.JobCategory, x.Language, x.CreatedAt })
            .HasDatabaseName("ix_admin_rubric_preview_runs_scope_created");

        // Khoá chống double-click: đúng MỘT lượt đang chạy cho mỗi (nghề, ngôn ngữ, phiên bản).
        // Câu đọc "có lượt nào Running không" KHÔNG phải trọng tài — hai request vào cùng lúc đều đọc
        // ra "không có". Ràng buộc DB mới là.
        //
        // ⚠ Row Running mồ côi (tiến trình chết giữa lời gọi đồng bộ) sẽ khoá chết phạm vi đó ở 409
        // vĩnh viễn nếu không self-heal — xem `ResolveStaleRunningAsync`.
        e.HasIndex(x => new { x.JobCategory, x.Language, x.RubricVersion })
            .IsUnique()
            .HasFilter("status = 'Running'")
            .HasDatabaseName("ux_admin_rubric_preview_runs_running");
    }
}
