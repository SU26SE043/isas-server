using Isas.InterviewService.Entities;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;

namespace Isas.InterviewService.Configurations;

public class RoadmapLevelThresholdConfiguration : IEntityTypeConfiguration<RoadmapLevelThreshold>
{
    public void Configure(EntityTypeBuilder<RoadmapLevelThreshold> e)
    {
        e.ToTable("roadmap_level_thresholds", t =>
            // Ngưỡng ngoài [0,100] là vô nghĩa: percentage so sánh với nó luôn nằm trong [0,100],
            // nên 101 = "không ai đạt được" và -1 = "ai cũng đạt" — cả hai đều là cấu hình hỏng mà
            // không có triệu chứng nào ngoài kết luận sai hiển thị cho người học. Guard ở tầng code
            // (service) VÀ tầng DB vì đây là con số quyết định câu "Đạt/Chưa đạt".
            t.HasCheckConstraint(
                "ck_roadmap_level_thresholds_pct_range",
                "threshold_pct >= 0 AND threshold_pct <= 100"));

        e.HasKey(x => x.Id);

        e.Property(x => x.Level).HasMaxLength(32).IsRequired();
        e.Property(x => x.ThresholdPct).IsRequired();
        e.Property(x => x.UpdatedBy).IsRequired();
        e.Property(x => x.UpdatedAt).IsRequired();

        // Đúng MỘT hàng cho mỗi cấp độ. Hai hàng cùng cấp thì đường đọc chọn hàng nào là KHÔNG XÁC
        // ĐỊNH — ngưỡng đạt sẽ đổi theo thứ tự trả về của planner. Ép ở tầng DB để hai request PUT
        // đồng thời có một bên thua bằng lỗi UNIQUE, thay vì cùng ghi rồi im lặng phân nhánh.
        e.HasIndex(x => x.Level).IsUnique();
    }
}
