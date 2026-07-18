using Isas.InterviewService.Entities;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;

namespace Isas.InterviewService.Configurations;

/// <summary>
/// DB26 — file_records trước nay KHÔNG có index nào ngoài PK, trong khi
/// <c>StorageService.GetFilesByUserId</c> lọc <c>user_id</c> rồi <c>ToListAsync()</c> nguyên entity
/// (kèm cột TEXT <c>parsed_text</c>) và được gọi từ endpoint user-facing ⇒ mỗi request là seq scan
/// toàn bảng + kéo theo TOAST fetch cho từng row khớp. Thêm index trên cột lọc.
///
/// user_id = Guid lỏng xuyên service (GEN-2, không FK) nên đây là index thường, không phải FK index.
/// Chỉ khai index — mọi mapping khác của FileRecord giữ nguyên theo convention.
/// </summary>
public class FileRecordConfiguration : IEntityTypeConfiguration<FileRecord>
{
    public void Configure(EntityTypeBuilder<FileRecord> e)
    {
        e.HasIndex(x => x.UserId)
            .HasDatabaseName("ix_file_records_user_id");
    }
}
