using Isas.InterviewService.Entities;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;

namespace Isas.InterviewService.Configurations;

public class PracticeFocusEventConfiguration : IEntityTypeConfiguration<PracticeFocusEvent>
{
    public void Configure(EntityTypeBuilder<PracticeFocusEvent> e)
    {
        e.HasKey(x => x.Id);

        e.Property(x => x.SignalType).HasMaxLength(32).IsRequired();
        e.Property(x => x.Note).HasMaxLength(256);
        e.Property(x => x.OccurredAt).IsRequired();

        // Tập ĐÓNG chốt ở tầng DB cho MỌI đường ghi — đối xứng ck_session_criterion_evidence_state.
        // Guard C# ở service chặn được đường HTTP; CHECK chặn cả đường nào đó về sau ghi thẳng DbSet.
        // Giá trị phải khớp FocusSignals.Allowed (có test khoá hai đầu).
        e.ToTable(t => t.HasCheckConstraint(
            "ck_practice_focus_events_signal_type",
            "signal_type IN ('tab_switch', 'paste', 'focus_lost')"));

        // Hình truy vấn duy nhất: gom theo (buổi, loại tín hiệu) để dựng tổng hợp cho màn kết quả.
        e.HasIndex(x => new { x.SessionId, x.SignalType });

        // Cascade: xoá buổi là xoá sạch dấu vết. FK NỘI service (cùng DB) nên không vi phạm GEN-2.
        // CỐ Ý không khai navigation collection ở PracticeSession: buổi có thể mang tới 500 dòng,
        // một `.Include(s => s.FocusEvents)` vô tình ở đường đọc nóng sẽ kéo cả tập về RAM.
        e.HasOne(x => x.Session)
            .WithMany()
            .HasForeignKey(x => x.SessionId)
            .OnDelete(DeleteBehavior.Cascade);
    }
}
