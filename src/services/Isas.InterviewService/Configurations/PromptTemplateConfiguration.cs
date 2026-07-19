using Isas.InterviewService.Entities;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;

namespace Isas.InterviewService.Configurations;

public class PromptTemplateConfiguration : IEntityTypeConfiguration<PromptTemplate>
{
    public void Configure(EntityTypeBuilder<PromptTemplate> e)
    {
        e.ToTable("prompt_templates", t =>
            // Version bắt đầu từ 1 và chỉ tăng. CHECK ở tầng DB vì đây là thứ
            // answer_scores.prompt_version trỏ tới — version 0/âm nghĩa là con dấu vô nghĩa.
            t.HasCheckConstraint("ck_prompt_templates_version_positive", "version > 0"));

        e.HasKey(x => x.Id);

        e.Property(x => x.Key).HasMaxLength(64).IsRequired();
        e.Property(x => x.Body).IsRequired();
        e.Property(x => x.Version).IsRequired();
        e.Property(x => x.IsActive).IsRequired();
        e.Property(x => x.UpdatedBy).IsRequired();
        e.Property(x => x.ChangeNote).HasMaxLength(512);

        // Append-only: mỗi (key, version) chỉ tồn tại một lần. Đây là hàng rào ở tầng DB cho
        // bất biến mà UpsertAsync giữ ở tầng code — hai request sửa cùng lúc sẽ có một bên
        // thua bằng lỗi UNIQUE thay vì cùng ghi version trùng rồi lịch sử phân nhánh im lặng.
        e.HasIndex(x => new { x.Key, x.Version }).IsUnique();

        // Đúng 1 bản active mỗi khoá. Partial index (Npgsql) — SQLite trong test không dựng
        // filtered unique giống hệt, nên UpsertAsync vẫn phải tự bảo đảm trong transaction.
        e.HasIndex(x => x.Key)
            .IsUnique()
            .HasFilter("is_active")
            .HasDatabaseName("ux_prompt_templates_active_key");
    }
}
